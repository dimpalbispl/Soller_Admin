using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SolarPortal.Application.Interfaces.Services;
using SolarPortal.Infrastructure.Data;
using System.Data;

namespace SolarPortal.Infrastructure.Services;

/// <summary>
/// Runs dbo.Sp_UpdateReaminingBV for one approved Remaining BV record.
///
/// Raw ADO rather than EF: Repurchincome, M_MemberMaster and M_SessnMaster are
/// legacy tables that are not in this app's EF model and migrations must never
/// touch them — the same pattern as LegacyMlmApprovalService and
/// IncCommissionCreditService.
///
/// The bill number is a random 6-digit number built from the digits 1-9, which
/// is exactly what the legacy admin's GenerateRandomStringactive(6) produced.
/// Unlike the legacy code we check it against Repurchincome first and retry, the
/// same way LegacyProductRequestService allocates an OrderNo — a repeated bill
/// number is invisible in the UI but makes the ledger impossible to reconcile.
/// </summary>
public class RemainingBvIncomeService : IRemainingBvIncomeService
{
    private readonly IConfiguration _config;
    private readonly ApplicationDbContext _db;   // only for the connection-string fallback
    private readonly ILogger<RemainingBvIncomeService> _log;

    private const string BillNoDigits = "123456789";
    private const int BillNoLength = 6;
    private const int BillNoAttempts = 5;

    /// <summary>
    /// Repurchincome.Remarks is varchar(150) — NARROWER than the 250 the entity's
    /// ProductName allows and than the procedure's @productName parameter. The
    /// remark is trimmed to this before it is sent, otherwise a long plan name
    /// fails the insert with "String or binary data would be truncated".
    /// </summary>
    private const int RemarksMaxLength = 150;

    public RemainingBvIncomeService(
        IConfiguration config,
        ApplicationDbContext db,
        ILogger<RemainingBvIncomeService> log)
    {
        _config = config;
        _db = db;
        _log = log;
    }

    private string? ConnStr => _config.GetConnectionString("DefaultConnection")
                            ?? _db.Database.GetConnectionString();

    public async Task<RemainingBvPostResult> PostAsync(
        int rbvId, string memberIdNo, decimal remainingBv, string? productName)
    {
        var result = new RemainingBvPostResult();

        var connStr = ConnStr;
        if (string.IsNullOrWhiteSpace(connStr))
        {
            result.Message = "No connection string configured for the Remaining BV ledger post.";
            return result;
        }
        if (string.IsNullOrWhiteSpace(memberIdNo))
        {
            result.Message = "The record has no member ID number, so the BV cannot be posted.";
            return result;
        }
        if (remainingBv <= 0m)
        {
            result.Message = "There is no remaining BV on this record to post.";
            return result;
        }

        var remarks = (productName ?? string.Empty).Trim();
        if (remarks.Length > RemarksMaxLength) remarks = remarks[..RemarksMaxLength];

        try
        {
            await using var conn = new SqlConnection(connStr);
            await conn.OpenAsync();

            var billNo = await AllocateBillNoAsync(conn);
            if (billNo == null)
            {
                result.Message = $"Could not allocate a unique bill number after {BillNoAttempts} attempts. Try approving again.";
                return result;
            }

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "dbo.Sp_UpdateReaminingBV";
            cmd.CommandType = CommandType.StoredProcedure;

            cmd.Parameters.Add("@IDNo", SqlDbType.VarChar, 50).Value = memberIdNo;

            var bv = cmd.Parameters.Add("@FinalBV", SqlDbType.Decimal);
            bv.Precision = 18;
            bv.Scale = 2;
            bv.Value = remainingBv;

            var bill = cmd.Parameters.Add("@BillNo", SqlDbType.Decimal);
            bill.Precision = 24;
            bill.Scale = 0;
            bill.Value = decimal.Parse(billNo);

            cmd.Parameters.Add("@productName", SqlDbType.VarChar, RemarksMaxLength).Value = remarks;
            cmd.Parameters.Add("@RbvId", SqlDbType.Int).Value = rbvId;

            // The procedure's last statement is SELECT @Msg AS Result — 'SUCCESS'
            // or 'FAILED: <reason>'. It swallows its own errors so the reason can
            // be shown to the admin instead of a raw SQL exception.
            var raw = (await cmd.ExecuteScalarAsync())?.ToString() ?? string.Empty;

            result.BillNo = billNo;
            result.Posted = raw.StartsWith("SUCCESS", StringComparison.OrdinalIgnoreCase);
            result.Message = result.Posted
                ? $"Remaining BV {remainingBv:N2} posted to the MLM ledger against bill no {billNo}."
                : $"The MLM ledger refused this record: {(raw.Length == 0 ? "no response from Sp_UpdateReaminingBV" : raw)}";

            if (!result.Posted)
                _log.LogWarning("Sp_UpdateReaminingBV returned '{Result}' for RbvId={RbvId}, IdNo={IdNo}, BV={Bv}",
                    raw, rbvId, memberIdNo, remainingBv);

            return result;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Sp_UpdateReaminingBV failed for RbvId={RbvId}, IdNo={IdNo}, BV={Bv}",
                rbvId, memberIdNo, remainingBv);
            result.Message = $"The MLM ledger post failed: {ex.Message}";
            return result;
        }
    }

    /// <summary>
    /// A 6-digit bill number that is not already in Repurchincome, or null if
    /// five draws all collided.
    /// </summary>
    private static async Task<string?> AllocateBillNoAsync(SqlConnection conn)
    {
        for (var i = 0; i < BillNoAttempts; i++)
        {
            var candidate = NewBillNo();

            // Compared as TEXT on both sides on purpose. Repurchincome.BillNo is a
            // varchar column that already holds non-numeric values — LegacyMlmApprovalService
            // writes "Order {orderNo}" into it. Matching it against a numeric parameter
            // makes SQL Server convert the whole column instead, which fails on those
            // rows with "Error converting data type varchar to numeric". The CAST keeps
            // this correct whichever type the column turns out to be.
            await using var check = conn.CreateCommand();
            check.CommandText = "SELECT COUNT(*) FROM Repurchincome WHERE CAST(BillNo AS varchar(50)) = @b";
            check.Parameters.Add("@b", SqlDbType.VarChar, 50).Value = candidate;

            var count = Convert.ToInt32(await check.ExecuteScalarAsync() ?? 0);
            if (count == 0) return candidate;
        }
        return null;
    }

    private static string NewBillNo()
    {
        var chars = new char[BillNoLength];
        for (var i = 0; i < BillNoLength; i++)
            chars[i] = BillNoDigits[Random.Shared.Next(BillNoDigits.Length)];
        return new string(chars);
    }
}
