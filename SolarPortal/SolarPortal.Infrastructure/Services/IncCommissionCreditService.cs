using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using SolarPortal.Application.Interfaces.Services;
using SolarPortal.Domain.Enums;
using SolarPortal.Infrastructure.Data;

namespace SolarPortal.Infrastructure.Services;

/// <summary>
/// Admin-side port of the installer panel's IncWalletService.CreditInstallationCommissionAsync.
///
/// It is a deliberate DUPLICATE of that method rather than a variation: the two
/// apps are separate deployments over one database, and installation commission
/// must behave identically whichever one posts it. Anything that differs here —
/// the ledger table, the duplicate check, the voucher columns, the RefNo format —
/// would let the same project be paid twice, because each side's "already paid?"
/// check would miss the other side's row.
///
/// Raw SQL, not EF: IncTrnvoucher and IncCommissionLedger are legacy tables that
/// are not in this app's EF model, and migrations must never touch them. Same
/// pattern as the other legacy writes in this project.
/// </summary>
public class IncCommissionCreditService : IIncCommissionCreditService
{
    private readonly IConfiguration _config;
    private readonly ApplicationDbContext _db;   // only for the connection-string fallback

    /// <summary>Wallet code in IncVouchertype — 'I' is the INC wallet.</summary>
    private const string IncWalletAcType = "I";

    public IncCommissionCreditService(IConfiguration config, ApplicationDbContext db)
    {
        _config = config;
        _db = db;
    }

    private string? ConnStr => _config.GetConnectionString("DefaultConnection")
                            ?? _db.Database.GetConnectionString();

    public async Task<IncCommissionCreditResult> CreditForRequestAsync(
        int solarRequestId, int workerId, string performedBy)
    {
        var result = new IncCommissionCreditResult();
        var connStr = ConnStr;
        if (string.IsNullOrWhiteSpace(connStr) || solarRequestId <= 0 || workerId <= 0)
        {
            result.Message = "No commission was credited: missing request or installer.";
            return result;
        }

        await using var conn = new SqlConnection(connStr);
        await conn.OpenAsync();

        // Only INC workers earn commission — a JOB worker does the same job on a
        // salary. Read the type from Workers rather than trusting anything the
        // caller passed in, because admin can switch a worker's type at any time.
        await using (var wt = conn.CreateCommand())
        {
            wt.CommandText = "SELECT Type FROM dbo.Workers WHERE Id = @w AND IsDeleted = 0";
            wt.Parameters.Add(new SqlParameter("@w", workerId));
            var t = await wt.ExecuteScalarAsync();
            if (t == null || t == DBNull.Value)
            {
                result.Message = "No commission was credited: installer not found.";
                return result;
            }
            if (Convert.ToInt32(t) != (int)WorkerType.INC)
            {
                result.Message = "No commission applies: the assigned installer is a JOB worker (salaried).";
                return result;
            }
        }

        // THE shared idempotency check. Keyed on the request alone, exactly as the
        // installer panel does it, so neither app can pay what the other already
        // paid. Do not change this key without changing it on both sides.
        await using (var dup = conn.CreateCommand())
        {
            dup.CommandText = "SELECT COUNT(1) FROM dbo.IncCommissionLedger WHERE SolarRequestId = @r";
            dup.Parameters.Add(new SqlParameter("@r", solarRequestId));
            if (Convert.ToInt32(await dup.ExecuteScalarAsync() ?? 0) > 0)
            {
                result.Message = "Commission for this project had already been credited.";
                return result;
            }
        }

        int? projectId;
        string requestNumber;
        await using (var q = conn.CreateCommand())
        {
            q.CommandText = "SELECT SolarProjectId, RequestNumber FROM dbo.SolarRequests WHERE Id = @r";
            q.Parameters.Add(new SqlParameter("@r", solarRequestId));
            await using var rd = await q.ExecuteReaderAsync();
            if (!await rd.ReadAsync())
            {
                result.Message = "No commission was credited: request not found.";
                return result;
            }
            projectId = rd.IsDBNull(0) ? null : Convert.ToInt32(rd.GetValue(0));
            requestNumber = rd.IsDBNull(1) ? string.Empty : (rd.GetValue(1)?.ToString() ?? string.Empty).Trim();
        }

        if (projectId is null or 0)
        {
            result.Message = "No commission was credited: this project has no solar plan attached.";
            return result;
        }

        // Flat amount configured on the plan — credited exactly, no percentage
        // maths and no deduction. (TDS applies only to per-connection income.)
        decimal commission = 0m;
        await using (var q = conn.CreateCommand())
        {
            q.CommandText = "SELECT IncCommissionAmount FROM dbo.SolarProjects WHERE Id = @p";
            q.Parameters.Add(new SqlParameter("@p", projectId.Value));
            var v = await q.ExecuteScalarAsync();
            if (v != null && v != DBNull.Value) commission = Convert.ToDecimal(v);
        }

        if (commission <= 0m)
        {
            result.Message = "No commission amount is set on this plan, so nothing was credited. " +
                             "Set it under INC Commission, then re-approve.";
            return result;
        }

        var narration = $"INC commission for {requestNumber}";
        var refNo = $"INC/{requestNumber}";

        // Ledger row and wallet voucher go together or not at all — a ledger row
        // without the voucher would permanently block the retry, since the
        // duplicate check above reads the ledger.
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync();
        try
        {
            await using (var ins = conn.CreateCommand())
            {
                ins.Transaction = tx;
                ins.CommandText = @"
INSERT INTO dbo.IncCommissionLedger
    (WorkerId, SolarRequestId, GrossAmount, TdsPercent, TdsAmount, NetAmount, CreditedAt, Status, Notes)
VALUES
    (@w, @r, @amt, 0, 0, @amt, @now, 'Credited', @notes)";
                ins.Parameters.Add(new SqlParameter("@w", workerId));
                ins.Parameters.Add(new SqlParameter("@r", solarRequestId));
                ins.Parameters.Add(new SqlParameter("@amt", commission));
                ins.Parameters.Add(new SqlParameter("@now", DateTime.Now));
                ins.Parameters.Add(new SqlParameter("@notes",
                    $"{narration}. Installation photos approved by {performedBy}."));
                await ins.ExecuteNonQueryAsync();
            }

            await PostVoucherAsync(conn, tx, creditTo: workerId.ToString(), amount: commission,
                                   narration: narration, refNo: refNo, workerId: workerId,
                                   date: DateTime.Now);

            await tx.CommitAsync();
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }

        result.Credited = true;
        result.Amount = commission;
        result.Message = $"Rs {commission:N0} commission credited to the installer's INC wallet.";
        return result;
    }

    /// <summary>
    /// Writes one credit row into IncTrnvoucher. VoucherNo continues the running
    /// series; UPDLOCK/HOLDLOCK stops two concurrent posts taking the same number.
    /// Column-for-column identical to the installer panel's writer.
    /// </summary>
    private static async Task PostVoucherAsync(SqlConnection conn, SqlTransaction tx,
        string creditTo, decimal amount, string narration, string refNo, int workerId, DateTime date)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
INSERT INTO dbo.IncTrnvoucher
    (VoucherNo, VoucherDate, DrTo, CrTo, Amount, Narration, RefNo, AcType,
     RecTimeStamp, VType, SessID, WSessID, Balance, UserId, FromID)
SELECT ISNULL(MAX(VoucherNo), 0) + 1, @vdate, '0', @crTo, @amt, @narr, @ref, @actype,
       GETDATE(), 'C', @sess, 1, 0, @uid, NULL
FROM dbo.IncTrnvoucher WITH (UPDLOCK, HOLDLOCK)";
        cmd.Parameters.Add(new SqlParameter("@vdate", date.Date));
        cmd.Parameters.Add(new SqlParameter("@crTo", creditTo));
        cmd.Parameters.Add(new SqlParameter("@amt", amount));
        cmd.Parameters.Add(new SqlParameter("@narr", narration));
        cmd.Parameters.Add(new SqlParameter("@ref", refNo));
        cmd.Parameters.Add(new SqlParameter("@actype", IncWalletAcType));
        cmd.Parameters.Add(new SqlParameter("@sess", Convert.ToDecimal(date.ToString("yyyyMMdd"))));
        cmd.Parameters.Add(new SqlParameter("@uid", workerId));
        await cmd.ExecuteNonQueryAsync();
    }
}
