using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using SolarPortal.Application.Interfaces.Services;
using SolarPortal.Infrastructure.Data;

namespace SolarPortal.Infrastructure.Services;

/// <summary>
/// Writes and reads the member's SOLAR wallet ledger (dbo.SolarTrnvoucher).
///
/// Column conventions are copied from the wallet ledgers already in this
/// database, so a solar row reads exactly like an INC or legacy one:
///   • credit → DrTo '0',    CrTo member, VType 'C'
///   • debit  → DrTo member, CrTo '0',    VType 'D'
///   • AcType → the wallet the row belongs to; 'S' is the Solar Wallet
///   • VoucherId is IDENTITY, VoucherNo is not, so it is taken as MAX + 1
///
/// The account is the member's IdNo (the same value SolarRequests.UserId holds),
/// not the legacy FormNo: this ledger belongs to the solar panel and every screen
/// here works in IdNo.
///
/// Raw SQL rather than EF: SolarTrnvoucher is created out-of-band by
/// ADD-SolarWallet.sql and is deliberately not part of the EF model.
/// </summary>
public class SolarWalletService : ISolarWalletService
{
    private readonly IConfiguration _config;
    private readonly ApplicationDbContext _db;   // connection-string fallback

    public SolarWalletService(IConfiguration config, ApplicationDbContext db)
    {
        _config = config;
        _db = db;
    }

    private string? ConnStr => _config.GetConnectionString("DefaultConnection")
                            ?? _db.Database.GetConnectionString();

    public async Task<bool> PostAsync(string memberIdNo, decimal amount, bool isCredit, string refNo, string narration)
    {
        var account = (memberIdNo ?? string.Empty).Trim();
        if (account.Length == 0 || amount <= 0m) return false;

        var connStr = ConnStr;
        if (string.IsNullOrWhiteSpace(connStr)) return false;

        // RefNo + direction is the duplicate guard: a replayed verify or a
        // double-clicked approve can never post the same money twice.
        const string sql = @"
IF NOT EXISTS (SELECT 1 FROM dbo.SolarTrnvoucher WHERE RefNo = @refNo AND VType = @vtype)
BEGIN
    INSERT INTO dbo.SolarTrnvoucher
        (VoucherNo, VoucherDate, DrTo, CrTo, Amount, Narration, RefNo,
         AcType, RecTimeStamp, VType, SessID, WSessID, Balance, UserId, FromID)
    SELECT
        ISNULL(MAX(VoucherNo), 0) + 1,
        CAST(CONVERT(varchar(8), GETDATE(), 112) AS datetime),
        @drTo,
        @crTo,
        @amount,
        @narration,
        @refNo,
        @acType,
        GETDATE(),
        @vtype,
        CAST(CONVERT(varchar(8), GETDATE(), 112) AS numeric(18,0)),
        1,
        0,
        0,
        NULL
    FROM dbo.SolarTrnvoucher;
    SELECT 1;
END
ELSE
    SELECT 0;";

        await using var conn = new SqlConnection(connStr);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.Add(new SqlParameter("@drTo",      isCredit ? "0" : account));
        cmd.Parameters.Add(new SqlParameter("@crTo",      isCredit ? account : "0"));
        cmd.Parameters.Add(new SqlParameter("@vtype",     isCredit ? "C" : "D"));
        cmd.Parameters.Add(new SqlParameter("@amount",    amount));
        cmd.Parameters.Add(new SqlParameter("@narration", (object?)narration ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@refNo",     refNo));
        cmd.Parameters.Add(new SqlParameter("@acType",    ISolarWalletService.SolarWalletAcType));

        var written = await cmd.ExecuteScalarAsync();
        return written != null && written != DBNull.Value && Convert.ToInt32(written) == 1;
    }

    public Task<bool> CreditVerifiedPaymentAsync(int paymentId, string memberIdNo, decimal amount,
                                                 string? requestNumber, string? utrNumber)
    {
        // Keyed on the payment id alone: the same payment verified again (or a
        // retried request) finds the row already there and writes nothing.
        var refNo = $"PAY/{paymentId}";
        var narration = $"Solar payment received for {requestNumber ?? "solar request"}"
                      + (string.IsNullOrWhiteSpace(utrNumber) ? "" : $" (UTR {utrNumber})")
                      + $" · Member ID {memberIdNo}";
        return PostAsync(memberIdNo, amount, isCredit: true, refNo: refNo, narration: narration);
    }

    public async Task<List<SolarWalletOption>> GetWalletsAsync()
    {
        var list = new List<SolarWalletOption>();
        var connStr = ConnStr;
        if (string.IsNullOrWhiteSpace(connStr)) return list;

        try
        {
            await using var conn = new SqlConnection(connStr);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Acid, WalletName, Actype FROM dbo.SolarVouchertype WHERE ISNULL(ActiveStatus,'Y') = 'Y' ORDER BY Acid";
            await using var rd = await cmd.ExecuteReaderAsync();
            while (await rd.ReadAsync())
            {
                list.Add(new SolarWalletOption(
                    Convert.ToInt32(rd["Acid"]),
                    (rd["WalletName"]?.ToString() ?? string.Empty).Trim(),
                    (rd["Actype"]?.ToString() ?? "S").Trim()));
            }
        }
        catch
        {
            // Table not created yet — the page still renders, with no wallet to pick.
        }
        return list;
    }

    public async Task<decimal> GetBalanceAsync(string memberIdNo)
    {
        var account = (memberIdNo ?? string.Empty).Trim();
        if (account.Length == 0) return 0m;

        var connStr = ConnStr;
        if (string.IsNullOrWhiteSpace(connStr)) return 0m;

        try
        {
            await using var conn = new SqlConnection(connStr);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT ISNULL(SUM(CASE WHEN VType = 'C' AND LTRIM(RTRIM(CrTo)) = @id THEN Amount
                       WHEN VType <> 'C' AND LTRIM(RTRIM(DrTo)) = @id THEN -Amount
                       ELSE 0 END), 0)
FROM dbo.SolarTrnvoucher
WHERE AcType = @acType";
            cmd.Parameters.Add(new SqlParameter("@id", account));
            cmd.Parameters.Add(new SqlParameter("@acType", ISolarWalletService.SolarWalletAcType));
            var v = await cmd.ExecuteScalarAsync();
            return v == null || v == DBNull.Value ? 0m : Convert.ToDecimal(v);
        }
        catch
        {
            return 0m;
        }
    }
}
