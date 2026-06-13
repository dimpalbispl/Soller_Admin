using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SolarPortal.Application.Services;
using SolarPortal.Infrastructure.Data;

namespace SolarPortal.Infrastructure.Services;

/// <summary>
/// Inserts "With Activation" submissions into the legacy SolFit table
/// TrnProductorderDetail via raw ADO.NET, mirroring the original VB
/// page ProductWalletRequest.aspx.vb (tp=A branch).
///
/// Reads the picked product's master row from [V#SpProductDetail] to get
/// MRP/DP/BV/PV/ShippingAmount/ProductName/ShippingProdid — same view the
/// new "With Activation" UI uses for the product cards. The insert mirrors
/// the legacy column list exactly so existing reports keep working.
///
/// We do NOT fail the user submission if the legacy insert errors — the
/// new SolarRequest record is the source of truth for the new workflow.
/// Legacy bridge errors are logged and surfaced as a warning so admin can
/// reconcile manually if needed.
/// </summary>
public class LegacyProductRequestService : ILegacyProductRequestService
{
    private readonly IConfiguration _config;
    private readonly ApplicationDbContext _db;
    private readonly ILogger<LegacyProductRequestService> _log;

    public LegacyProductRequestService(
        IConfiguration config,
        ApplicationDbContext db,
        ILogger<LegacyProductRequestService> log)
    {
        _config = config;
        _db     = db;
        _log    = log;
    }

    public async Task<LegacyInsertResult> InsertWithActivationAsync(LegacyProductRequestInput input)
    {
        var result = new LegacyInsertResult();
        var connStr = _config.GetConnectionString("DefaultConnection")
                   ?? _db.Database.GetConnectionString();
        if (string.IsNullOrWhiteSpace(connStr))
        {
            result.ErrorMessage = "No connection string configured for legacy bridge.";
            return result;
        }

        try
        {
            await using var conn = new SqlConnection(connStr);
            await conn.OpenAsync();

            // ─── 1. Resolve Formno from IdNo ─────────────────────────────
            // The legacy table is keyed by Formno (numeric), but the new app
            // identifies users by IdNo (e.g. "SE86372259"). m_membermaster
            // bridges the two — and we already login via that table.
            long formNo;
            await using (var cmdF = new SqlCommand(
                "SELECT TOP 1 Formno FROM M_MemberMaster WHERE Idno = @id", conn))
            {
                cmdF.Parameters.AddWithValue("@id", input.MemberIdNo ?? string.Empty);
                var fObj = await cmdF.ExecuteScalarAsync();
                if (fObj == null || fObj == DBNull.Value)
                {
                    result.ErrorMessage = $"Legacy bridge: member IdNo '{input.MemberIdNo}' not found in M_MemberMaster.";
                    _log.LogWarning(result.ErrorMessage);
                    return result;
                }
                formNo = Convert.ToInt64(fObj);
            }

            // ─── 2. Generate a unique OrderNo ────────────────────────────
            // The legacy VB code uses {6 random digits}+{Formno} and retries
            // on collision. We do the same up to 5 attempts.
            string? orderNo = null;
            var rng = new Random();
            for (int i = 0; i < 5; i++)
            {
                var candidate = $"{rng.Next(100000, 999999)}{formNo}";
                await using var cmdC = new SqlCommand(
                    "SELECT COUNT(*) FROM TrnOrder WHERE Orderno = @o", conn);
                cmdC.Parameters.AddWithValue("@o", candidate);
                var cnt = (int)(await cmdC.ExecuteScalarAsync() ?? 0);
                if (cnt == 0) { orderNo = candidate; break; }
            }
            if (orderNo == null)
            {
                result.ErrorMessage = "Legacy bridge: couldn't allocate a unique OrderNo after 5 attempts.";
                _log.LogWarning(result.ErrorMessage);
                return result;
            }

            // ─── 3. Insert the order-detail row ──────────────────────────
            // Column list matches the legacy VB Insert exactly. We pull
            // product master fields from [V#SpProductDetail] inline so the
            // BV/PV/MRP/DP/Shipping numbers are authoritative (same source
            // the With Activation cards display).
            const string insertSql = @"
INSERT INTO TrnProductorderDetail
    (OrderNo, FormNo, ProductID, Qty, Rate, NetAmount, RecTimeStamp,
     DispDate, DispStatus, DispQty, RemQty, DispAmt,
     MRP, DP, ProductName, ImgPath, RP, BV,
     FSEssId, Prodtype, PV, txnid, txndate, ImageUpload,
     ForType, PID,
     UserAddress, City, District, PinCode, UserState, StateCode)
SELECT
    @OrderNo, @FormNo, ProdId, @Qty, DP, DP * @Qty, GETDATE(),
    NULL, 'N', 0, @Qty, 0,
    MRP, DP, ProductName, '', 0, BV,
    (SELECT ISNULL(MAX(FsessID), 1) FROM solfitenergyinv..M_FiscalMaster),
    'P', PV, @TxnId, @TxnDate, @ImageFile,
    'A', @PayMode,
    @Addr, @City, @Dist, @Pin, @StateNm, @StateCd
FROM [V#SpProductDetail]
WHERE ProdId = @ProdId;";

            await using var cmdI = new SqlCommand(insertSql, conn);
            cmdI.Parameters.AddWithValue("@OrderNo",   orderNo);
            cmdI.Parameters.AddWithValue("@FormNo",    formNo);
            cmdI.Parameters.AddWithValue("@ProdId",    input.ProductId);
            cmdI.Parameters.AddWithValue("@Qty",       Math.Max(1, input.Qty));
            cmdI.Parameters.AddWithValue("@TxnId",     (object?)input.TxnId ?? DBNull.Value);
            cmdI.Parameters.AddWithValue("@TxnDate",   (object?)input.TxnDate ?? DBNull.Value);
            cmdI.Parameters.AddWithValue("@ImageFile", (object?)input.ImageFileName ?? DBNull.Value);
            cmdI.Parameters.AddWithValue("@PayMode",   input.PayModeId);
            cmdI.Parameters.AddWithValue("@Addr",      (object?)input.Address ?? DBNull.Value);
            cmdI.Parameters.AddWithValue("@City",      (object?)input.City ?? DBNull.Value);
            cmdI.Parameters.AddWithValue("@Dist",      (object?)input.District ?? DBNull.Value);
            cmdI.Parameters.AddWithValue("@Pin",       (object?)input.PinCode ?? DBNull.Value);
            cmdI.Parameters.AddWithValue("@StateNm",   (object?)input.StateName ?? DBNull.Value);
            cmdI.Parameters.AddWithValue("@StateCd",   (object?)input.StateCode ?? DBNull.Value);

            var rows = await cmdI.ExecuteNonQueryAsync();
            if (rows < 1)
            {
                result.ErrorMessage = $"Legacy bridge: product Id {input.ProductId} not found in V#SpProductDetail.";
                _log.LogWarning(result.ErrorMessage);
                return result;
            }

            result.Success = true;
            result.OrderNo = orderNo;
            _log.LogInformation("Legacy product order created: OrderNo={OrderNo}, FormNo={FormNo}, ProductId={ProdId}, Qty={Qty}",
                orderNo, formNo, input.ProductId, input.Qty);
            return result;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Legacy bridge insert failed for IdNo={IdNo}, ProductId={ProdId}",
                input.MemberIdNo, input.ProductId);
            result.ErrorMessage = $"Legacy bridge insert failed: {ex.Message}";
            return result;
        }
    }
}
