using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SolarPortal.Application.Services;
using SolarPortal.Infrastructure.Data;

namespace SolarPortal.Infrastructure.Services;

/// <summary>
/// Ports VB AprvAction(AprvType, ApprvStatus) from the legacy admin into
/// the new admin Payment Verification flow. Gated by RequestType=WithActivation
/// at the controller — this service assumes the caller has already decided
/// the legacy chain must run.
///
/// All SQL is parameterized (the original VB had string-concatenation
/// injection risk). The full chain runs inside a single SqlTransaction —
/// any single step failure rolls back the lot.
/// </summary>
public class LegacyMlmApprovalService : ILegacyMlmApprovalService
{
    private readonly IConfiguration _config;
    private readonly ApplicationDbContext _db;
    private readonly ILogger<LegacyMlmApprovalService> _log;

    // Legacy schema uses cross-DB references — Inventory tables live in solfitenergyinv.
    // Same constant the existing LegacyProductRequestService uses, kept here
    // so this file is self-contained.
    private const string InvDb = "solfitenergyinv";

    public LegacyMlmApprovalService(
        IConfiguration config,
        ApplicationDbContext db,
        ILogger<LegacyMlmApprovalService> log)
    {
        _config = config;
        _db     = db;
        _log    = log;
    }

    public async Task<LegacyMlmResult> ApproveActivationAsync(LegacyMlmApprovalInput input)
    {
        var result = new LegacyMlmResult();
        var connStr = _config.GetConnectionString("DefaultConnection")
                   ?? _db.Database.GetConnectionString();
        if (string.IsNullOrWhiteSpace(connStr))
        {
            result.ErrorMessage = "No connection string configured for legacy MLM bridge.";
            return result;
        }

        await using var conn = new SqlConnection(connStr);
        await conn.OpenAsync();
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync();

        try
        {
            // ─── 1. Resolve legacy activation row for this member ─────────
            // Lookup is "latest TrnProductorderDetail row with ForType='A' for
            // this member" — we no longer match by UTR. Per spec:
            //   "ek id ka ek baar hi chalna chahiye ye process with activation me"
            // Activation is a one-time event per member per activation request.
            // The latest legacy row IS that activation; multiple payments on
            // the same request share that one row.
            var (orderNo, currentStatus) = await FindLegacyActivationOrderAsync(conn, tx, input.MemberIdNo);
            if (string.IsNullOrEmpty(orderNo))
            {
                result.ErrorMessage =
                    $"Legacy MLM bridge: no activation row (ForType='A') in TrnProductorderDetail for IdNo '{input.MemberIdNo}'.";
                await tx.RollbackAsync();
                return result;
            }
            result.OrderNo = orderNo;

            // ─── 2. Idempotency gate ──────────────────────────────────────
            // Activation has already happened if IsApprove is not 'N':
            //   'Y' → already approved (Sp_ActivateMember_New already ran,
            //         Repurchincome ledger already inserted, member already
            //         activated). Running again would duplicate the MLM
            //         ledger and corrupt kit calculations.
            //   'R' → already rejected. Don't re-approve a rejected row.
            // Either way: silent success, no side effects.
            if (!string.Equals(currentStatus, "N", StringComparison.OrdinalIgnoreCase))
            {
                result.AlreadyProcessed = true;
                result.Success          = true;
                await tx.CommitAsync();
                _log.LogInformation(
                    "Legacy MLM approval skipped (idempotent — IsApprove='{Status}' already): OrderNo={OrderNo}, IdNo={IdNo}",
                    currentStatus, orderNo, input.MemberIdNo);
                return result;
            }

            // ─── 3. Member info (Formno, ActiveStatus, KitId) ─────────────
            var (formNo, productStatus, productKitId, memAddress) =
                await LookupMemberAsync(conn, tx, input.MemberIdNo);
            if (formNo <= 0)
            {
                result.ErrorMessage = $"Legacy MLM bridge: member IdNo '{input.MemberIdNo}' not found in M_MemberMaster.";
                await tx.RollbackAsync();
                return result;
            }

            // ─── 4. Compute BV/PV totals for this order ───────────────────
            var (orderBv, orderPv) = await ComputeOrderBvPvAsync(conn, tx, orderNo);

            // ─── 5. Existing cumulative PV in Repurchincome (threshold input) ──
            var existingPv = await ExecuteScalarDecimalAsync(conn, tx,
                "SELECT ISNULL(SUM(PVValue), 0) FROM Repurchincome WHERE FormNo = @f",
                p => p.AddWithValue("@f", formNo));

            // ─── 6. Current sessid from M_SessnMaster ──────────────────────
            var sessId = await ExecuteScalarIntAsync(conn, tx,
                "SELECT ISNULL(MAX(Sessid), 0) FROM M_SessnMaster",
                _ => { });

            // ─── 7. KitId / KitName for activation order (ForType='A') ─────
            //    select KitId,KitName from m_Kitmaster
            //    where TopUpSeq > 0 AND BV <= @amount AND BV > 0 AND ActiveStatus='Y'
            //    ORDER BY KitAmount DESC LIMIT 1
            var (kitId, kitName) = await ResolveActivationKitAsync(conn, tx, input.Amount);

            // ─── 8. Determine OrderType: 'O' if already active, 'T' if new ─
            var orderType = string.Equals(productStatus, "Y", StringComparison.OrdinalIgnoreCase) ? "O" : "T";

            // ─── 9. Approval audit remark (matches VB format) ──────────────
            var approveAudit = $"Approve Payment Request On ReqNo:{input.RequestNumber ?? "-"} for Idno:{input.MemberIdNo}";

            // Now the big sequence of inserts/updates — each is a separate
            // SqlCommand sharing the same transaction.

            // ─── 10. Insert TrnorderDetail rows ────────────────────────────
            // Single bulk INSERT...SELECT joining solfitenergyinv..M_ProductMaster
            // (cross-DB) for master data, vs the VB's row-by-row loop.
            await ExecuteAsync(conn, tx, $@"
INSERT INTO TrnorderDetail
    (OrderNo, FormNo, ProductID, Qty, Rate, NetAmount, RecTimeStamp,
     DispDate, DispStatus, DispQty, RemQty, DispAmt,
     MRP, DP, ProductName, ImgPath, RP, BV, FSEssId, Prodtype, PV, UserTypeAD)
SELECT
    @o, @f, p.Prodid, tpd.Qty, p.DP, p.DP * tpd.Qty, GETDATE(),
    NULL, 'N', 0, tpd.Qty, 0,
    p.MRP, p.DP, p.ProductName, '', 0, p.BV,
    (SELECT ISNULL(MAX(FsessID), 1) FROM {InvDb}..M_FiscalMaster),
    'P', p.PV, 'W'
FROM TrnProductorderDetail tpd
INNER JOIN {InvDb}..M_ProductMaster p ON p.Prodid = tpd.ProductID
WHERE tpd.OrderNo = @o
  AND tpd.IsApprove = 'N'
  AND p.ActiveStatus = 'Y'
  AND p.OnWebsite = 'Y';", p =>
            {
                p.AddWithValue("@o", orderNo);
                p.AddWithValue("@f", formNo);
            });

            // ─── 11. Insert TrnOrder header from M_MemberMaster ────────────
            await ExecuteAsync(conn, tx, @"
INSERT INTO TrnOrder
    (OrderNo, OrderDate, MemFirstName, MemLastName, Address1, Address2,
     CountryID, CountryName, StateCode, City, PinCode,
     Mobl, EMail, FormNo, UserType, Passw, PayMode, ChDDNo, ChDate, ChAmt,
     BankName, BranchName, Remark, OrderAmt, OrderItem, OrderQty, ActiveStatus,
     HostIp, RecTimeStamp, IsTransfer, DispatchDate, DispatchStatus, DispatchQty,
     RemainQty, DispatchAmount, Shipping, SessID, RewardPoint,
     CourierName, DocketNo, OrderFor, IsConfirm, OrderType, Discount, OldShipping,
     ShippingStatus, IdNo, FSessId, BankAmt, OtherAmt, WalletAmt,
     TravelPoint, KitName, ForVadicGurukul)
SELECT
    @o, CAST(CONVERT(varchar, GETDATE(), 106) AS DateTime),
    MemFirstName, MemLastName, Address1, Address2,
    CountryID, CountryName, StateCode, City,
    CASE WHEN PinCode = '' THEN 0 ELSE PinCode END,
    Mobl, EMail, @f, '',
    Passw, '', 0, '', 0, '', '', @remark,
    0, 0, 0, 'Y', 'H', GETDATE(), 'Y', '', 'N', 0,
    '0', 0, 0, @sessid, 0, '', 0, '', 'Y', @ordertype,
    0, @f, 'Y', @idno, '1',
    '0', '0', '0', 0, @kitname, 'N'
FROM M_MemberMaster
WHERE Formno = @f;", p =>
            {
                p.AddWithValue("@o",         orderNo);
                p.AddWithValue("@f",         formNo);
                p.AddWithValue("@remark",    input.ApproveRemark);
                p.AddWithValue("@sessid",    sessId);
                p.AddWithValue("@ordertype", orderType);
                p.AddWithValue("@idno",      input.MemberIdNo);
                p.AddWithValue("@kitname",   kitName ?? string.Empty);
            });

            // ─── 12. Update TrnOrder aggregates from TrnorderDetail ────────
            await ExecuteAsync(conn, tx, $@"
UPDATE a SET
    OrderAmt   = b.OrderAmount,
    OrderItem  = b.OrderItem,
    OrderQty   = b.OrderQty,
    RemainQty  = b.OrderQty,
    BV         = b.BVV_,
    WalletAmt  = b.OrderAmount,
    PV         = b.PVV_,
    Shipping   = b.ShippingAmt
FROM TrnOrder a,
(
    SELECT COUNT(*) OrderItem, SUM(Qty) AS OrderQty,
           SUM(NetAmount) AS OrderAmount,
           SUM(b.BV * a.Qty) AS BVV_,
           SUM(b.PV * a.Qty) AS PVV_,
           SUM(a.Qty) AS ShippingAmt
    FROM TrnorderDetail a
    INNER JOIN {InvDb}..M_ProductMaster b ON a.ProductID = b.ProdID
    WHERE a.OrderNo = @o AND a.FormNo = @f
) b
WHERE a.OrderNo = @o AND a.FormNo = @f;", p =>
            {
                p.AddWithValue("@o", orderNo);
                p.AddWithValue("@f", formNo);
            });

            // ─── 13. UserHistory audit row ─────────────────────────────────
            await ExecuteAsync(conn, tx, @"
INSERT INTO UserHistory (UserId, UserName, PageName, Activity, ModifiedFlds, RecTimeStamp, Memberid)
VALUES (@f, @idno, 'Product Request', 'Product Request',
        @hist, GETDATE(), @f);", p =>
            {
                p.AddWithValue("@f",    formNo);
                p.AddWithValue("@idno", input.MemberIdNo);
                p.AddWithValue("@hist", $" Product Request For Order No {orderNo} ");
            });

            // ─── 14. TrnPaymentConfirmation (cross-DB) ─────────────────────
            await ExecuteAsync(conn, tx, $@"
INSERT INTO {InvDb}..TrnPaymentConfirmation
    (SNo, ConfirmBy, OrderNo, FormNo, OrderAmt, IsConfirm, RecTimeStamp,
     UserID, OrderFor, IDNO, ActiveStatus, OrdType, FSessId)
SELECT
    CASE WHEN MAX(SNo) IS NULL THEN 1001 ELSE MAX(SNo) + 1 END,
    '', @o, @f, @amt, 'Y', GETDATE(),
    0, '', @idno, 'Y', 'D', 1
FROM {InvDb}..TrnPaymentConfirmation;", p =>
            {
                p.AddWithValue("@o",    orderNo);
                p.AddWithValue("@f",    formNo);
                p.AddWithValue("@amt",  input.Amount);
                p.AddWithValue("@idno", input.MemberIdNo);
            });

            // ─── 15. Repurchincome (MLM commission ledger) ─────────────────
            // VB has 3 branches based on ProductStatus + KitId. For activation
            // (the case we're in), the dominant branch is ProductStatus='N',
            // BillType='A', BType='A', FromID=formno. The Y-branches handle
            // repurchase flows that go through OTHER request modes — not us.
            await ExecuteAsync(conn, tx, @"
INSERT INTO Repurchincome
    (Sessid, Formno, BillNo, Billdate, Repurchincome, Imported, BillType, SoldBy,
     MSessid, KitId, Dsessid, Remarks, PVvalue, BType, FromID)
SELECT
    @sessid, @f, @billno, GETDATE(), @bv, 'N', 'A', @party,
    ISNULL(MAX(Sessid), 1), 0, CONVERT(varchar, GETDATE(), 112), '', @pv, 'A', @f
FROM M_MonthSessnMaster;", p =>
            {
                p.AddWithValue("@sessid", sessId);
                p.AddWithValue("@f",      formNo);
                p.AddWithValue("@billno", $"Order {orderNo}");
                p.AddWithValue("@bv",     orderBv);
                p.AddWithValue("@pv",     orderPv);
                p.AddWithValue("@party",  input.PartyCode ?? string.Empty);
            });

            // ─── 16. EXEC Sp_ActivateMember_New with the right kit level ───
            // Thresholds straight from the VB code:
            //   cumulative PV (existing + new) >= 4950  → kit 4
            //   1975 <= cumulative PV < 4950            → kit 5
            //   else                                    → kit 4
            // The procedure receives EXISTING PV (not cumulative) per the VB's
            // first parameter — Sp_ActivateMember_New computes the rest internally.
            var cumulativePv = existingPv + orderPv;
            int kitLevel = (cumulativePv >= 4950m) ? 4
                         : (cumulativePv >= 1975m) ? 5
                         : 4;
            await ExecuteAsync(conn, tx,
                "EXEC Sp_ActivateMember_New @idno, @o, @existingPv, @bv, @kit;", p =>
            {
                p.AddWithValue("@idno",       input.MemberIdNo);
                p.AddWithValue("@o",          orderNo);
                p.AddWithValue("@existingPv", existingPv);
                p.AddWithValue("@bv",         orderBv);
                p.AddWithValue("@kit",        kitLevel);
            });

            // ─── 17. Finally mark TrnProductorderDetail row as approved ────
            await ExecuteAsync(conn, tx, @"
UPDATE TrnProductorderDetail
SET IsApprove    = 'Y',
    Remark       = @rem,
    ApproveRemark= @arem,
    Approvedate  = GETDATE()
WHERE OrderNo = @o;", p =>
            {
                p.AddWithValue("@rem",  approveAudit);
                p.AddWithValue("@arem", input.ApproveRemark);
                p.AddWithValue("@o",    orderNo);
            });

            await tx.CommitAsync();
            result.Success = true;
            _log.LogInformation(
                "Legacy MLM approval committed: OrderNo={OrderNo}, FormNo={FormNo}, IdNo={IdNo}, BV={BV}, PV={PV}, KitLevel={Kit}",
                orderNo, formNo, input.MemberIdNo, orderBv, orderPv, kitLevel);
            return result;
        }
        catch (Exception ex)
        {
            try { await tx.RollbackAsync(); } catch { /* ignore rollback errors */ }
            _log.LogError(ex,
                "Legacy MLM approval failed for IdNo={IdNo}, UTR={Utr}, RequestNumber={RN}",
                input.MemberIdNo, input.Utr, input.RequestNumber);
            result.ErrorMessage = $"Legacy MLM approval failed: {ex.Message}";
            return result;
        }
    }

    public async Task<LegacyMlmResult> RejectActivationAsync(LegacyMlmRejectInput input)
    {
        var result = new LegacyMlmResult();
        var connStr = _config.GetConnectionString("DefaultConnection")
                   ?? _db.Database.GetConnectionString();
        if (string.IsNullOrWhiteSpace(connStr))
        {
            result.ErrorMessage = "No connection string configured for legacy MLM bridge.";
            return result;
        }

        await using var conn = new SqlConnection(connStr);
        await conn.OpenAsync();
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync();

        try
        {
            // ─── Same lookup + idempotency gate as ApproveActivationAsync ───
            // Per spec: "approve reject both side" — both endpoints must
            // respect the one-time-per-member rule.
            //
            // If the legacy row is already in a terminal state ('Y' or 'R'),
            // we DON'T touch it. Specifically:
            //   • Already 'Y' (approved) — reject would unwind an already-
            //     processed activation. The activation/Repurchincome/TrnOrder
            //     rows persist; flipping IsApprove='R' here would create an
            //     inconsistent state.
            //   • Already 'R' (rejected) — nothing to do.
            // In both cases we return success with AlreadyProcessed=true.
            var (orderNo, currentStatus) = await FindLegacyActivationOrderAsync(conn, tx, input.MemberIdNo);
            if (string.IsNullOrEmpty(orderNo))
            {
                result.ErrorMessage =
                    $"Legacy MLM bridge: no activation row (ForType='A') in TrnProductorderDetail for IdNo '{input.MemberIdNo}'.";
                await tx.RollbackAsync();
                return result;
            }
            result.OrderNo = orderNo;

            if (!string.Equals(currentStatus, "N", StringComparison.OrdinalIgnoreCase))
            {
                result.AlreadyProcessed = true;
                result.Success          = true;
                await tx.CommitAsync();
                _log.LogInformation(
                    "Legacy MLM reject skipped (idempotent — IsApprove='{Status}' already): OrderNo={OrderNo}, IdNo={IdNo}",
                    currentStatus, orderNo, input.MemberIdNo);
                return result;
            }

            var rejectAudit = $"Reject Payment Request On ReqNo:{input.RequestNumber ?? "-"} for Idno:{input.MemberIdNo}";

            await ExecuteAsync(conn, tx, @"
UPDATE TrnProductorderDetail
SET IsApprove    = 'R',
    Remark       = @rem,
    ApproveRemark= @arem,
    Rejectdate   = GETDATE()
WHERE OrderNo = @o;", p =>
            {
                p.AddWithValue("@rem",  rejectAudit);
                p.AddWithValue("@arem", input.RejectRemark);
                p.AddWithValue("@o",    orderNo);
            });

            await tx.CommitAsync();
            result.Success = true;
            _log.LogInformation("Legacy MLM reject committed: OrderNo={OrderNo}, IdNo={IdNo}", orderNo, input.MemberIdNo);
            return result;
        }
        catch (Exception ex)
        {
            try { await tx.RollbackAsync(); } catch { /* ignore */ }
            _log.LogError(ex, "Legacy MLM reject failed for UTR={Utr}", input.Utr);
            result.ErrorMessage = $"Legacy MLM reject failed: {ex.Message}";
            return result;
        }
    }

    // ════════════════════════════════════════════════════════════════════
    //  Internals
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Locate the legacy activation order for this member, WITHOUT filtering by
    /// IsApprove. Returns both the OrderNo and the current IsApprove value
    /// ('N' / 'Y' / 'R') so the caller can decide whether the chain has
    /// already been processed.
    ///
    /// Lookup rule: latest activation order (ForType='A') for the given IdNo,
    /// ordered by RecTimeStamp DESC. We do NOT use the payment UTR to match,
    /// because subsequent payments on the same With Activation request have
    /// different UTRs that don't appear in the legacy row (the legacy row was
    /// written once at request-creation time with the FIRST payment's UTR).
    /// Member + ForType + latest is the right key — activation is a per-member,
    /// per-request event, and the latest row corresponds to the current request.
    ///
    /// Reactivation flow:
    ///   • SCR-003 → legacy row 1, IsApprove='Y' (already processed)
    ///   • SCR-005 → legacy row 2 (newer RecTimeStamp), IsApprove='N'
    ///   This method returns row 2 — correct, because we want to process the
    ///   reactivation request, not re-process the old one.
    /// </summary>
    private async Task<(string? OrderNo, string? IsApprove)> FindLegacyActivationOrderAsync(
        SqlConnection conn, SqlTransaction tx, string idNo)
    {
        if (string.IsNullOrWhiteSpace(idNo)) return (null, null);
        const string sql = @"
SELECT TOP 1 tpd.OrderNo, tpd.IsApprove
FROM TrnProductorderDetail tpd
INNER JOIN M_MemberMaster m ON m.Formno = tpd.FormNo
WHERE m.Idno = @idno
  AND tpd.ForType = 'A'
ORDER BY tpd.RecTimeStamp DESC;";

        await using var cmd = new SqlCommand(sql, conn, tx);
        cmd.Parameters.AddWithValue("@idno", idNo);
        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) return (null, null);
        var orderNo  = r.IsDBNull(0) ? null : r.GetString(0);
        var approve  = r.IsDBNull(1) ? null : r.GetString(1);
        return (orderNo, approve);
    }

    private async Task<(long Formno, string? ActiveStatus, int KitId, string? Address1)>
        LookupMemberAsync(SqlConnection conn, SqlTransaction tx, string idNo)
    {
        const string sql = "SELECT TOP 1 Formno, ActiveStatus, ISNULL(Kitid, 0), Address1 FROM M_MemberMaster WHERE Idno = @idno";
        await using var cmd = new SqlCommand(sql, conn, tx);
        cmd.Parameters.AddWithValue("@idno", idNo);
        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) return (0, null, 0, null);

        // ===== Type coercion =====
        // Legacy schema stores M_MemberMaster.Formno as decimal/numeric (NOT bigint),
        // so SqlDataReader returns System.Decimal for it. Calling r.GetInt64(0)
        // on a decimal column throws:
        //   "Unable to cast object of type 'System.Decimal' to type 'System.Int64'"
        //
        // Convert.ToInt64(object) handles the boxing decimal -> long conversion
        // safely for any Formno value that fits in Int64 (which is every
        // realistic Formno — 6–8 digits typically).
        // ActiveStatus is char/varchar — GetString is safe.
        // Kitid was already done correctly via Convert.ToInt32(GetValue(...)).
        return (
            Convert.ToInt64(r.GetValue(0)),
            r.IsDBNull(1) ? null : r.GetString(1),
            Convert.ToInt32(r.GetValue(2)),
            r.IsDBNull(3) ? null : r.GetString(3));
    }

    private async Task<(decimal Bv, decimal Pv)> ComputeOrderBvPvAsync(SqlConnection conn, SqlTransaction tx, string orderNo)
    {
        const string sql = @"
SELECT ISNULL(SUM(BV), 0), ISNULL(SUM(PV), 0)
FROM TrnProductorderDetail
WHERE OrderNo = @o AND IsApprove = 'N';";
        await using var cmd = new SqlCommand(sql, conn, tx);
        cmd.Parameters.AddWithValue("@o", orderNo);
        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) return (0, 0);
        return (Convert.ToDecimal(r.GetValue(0)), Convert.ToDecimal(r.GetValue(1)));
    }

    private async Task<(int KitId, string? KitName)> ResolveActivationKitAsync(SqlConnection conn, SqlTransaction tx, decimal amount)
    {
        // VB: SELECT TOP 1 KitId, KitName FROM m_Kitmaster
        //     WHERE TopUpSeq > 0 AND BV <= @amount AND BV > 0 AND ActiveStatus='Y'
        //     ORDER BY KitAmount DESC
        const string sql = @"
SELECT TOP 1 KitId, KitName
FROM m_Kitmaster
WHERE TopUpSeq > 0 AND BV > 0 AND BV <= @amt AND ActiveStatus = 'Y'
ORDER BY KitAmount DESC;";
        await using var cmd = new SqlCommand(sql, conn, tx);
        cmd.Parameters.AddWithValue("@amt", amount);
        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) return (0, string.Empty);
        return (Convert.ToInt32(r.GetValue(0)), r.IsDBNull(1) ? string.Empty : r.GetString(1));
    }

    // Generic ExecuteNonQuery helper with parameter setup callback.
    private static async Task ExecuteAsync(SqlConnection conn, SqlTransaction tx, string sql,
                                            Action<SqlParameterCollection> setParams)
    {
        await using var cmd = new SqlCommand(sql, conn, tx);
        setParams(cmd.Parameters);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<int> ExecuteScalarIntAsync(SqlConnection conn, SqlTransaction tx, string sql,
                                                          Action<SqlParameterCollection> setParams)
    {
        await using var cmd = new SqlCommand(sql, conn, tx);
        setParams(cmd.Parameters);
        var v = await cmd.ExecuteScalarAsync();
        return v == null || v == DBNull.Value ? 0 : Convert.ToInt32(v);
    }

    private static async Task<decimal> ExecuteScalarDecimalAsync(SqlConnection conn, SqlTransaction tx, string sql,
                                                                  Action<SqlParameterCollection> setParams)
    {
        await using var cmd = new SqlCommand(sql, conn, tx);
        setParams(cmd.Parameters);
        var v = await cmd.ExecuteScalarAsync();
        return v == null || v == DBNull.Value ? 0m : Convert.ToDecimal(v);
    }
}
