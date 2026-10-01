using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using SolarPortal.Application.Interfaces.Services;
using SolarPortal.Infrastructure.Data;
using System.Data;

namespace SolarPortal.Infrastructure.Services;

/// <summary>
/// Reads the wallet ledger (IncTrnvoucher) for the admin's Wallet Transaction
/// report — every credit and debit, whoever wrote it: INC commission, an INC
/// withdrawal, or an Extra Payment Refund / debit.
///
/// Raw ADO like the other legacy reads: IncTrnvoucher, IncVouchertype and
/// M_MemberMaster are not in the EF model and migrations must never touch them.
///
/// Two lookups make a row readable:
///   * IncVouchertype on AcType — which wallet the row belongs to.
///   * The account column is a MEMBER IdNo for refunds and a WORKER id for INC
///     commission, so both are looked up and whichever matches supplies the name.
/// </summary>
public class WalletLedgerReportService : IWalletLedgerReportService
{
    private readonly IConfiguration _config;
    private readonly ApplicationDbContext _db;   // only for the connection-string fallback

    public WalletLedgerReportService(IConfiguration config, ApplicationDbContext db)
    {
        _config = config;
        _db = db;
    }

    private string? ConnStr => _config.GetConnectionString("DefaultConnection")
                            ?? _db.Database.GetConnectionString();

    private const string Sql = @"
SELECT TOP (@top)
    v.VoucherId,
    v.VoucherNo,
    v.VoucherDate,
    v.RecTimeStamp,
    v.AcType,
    t.WalletName,
    v.VType,
    v.Amount,
    v.Narration,
    v.RefNo,
    -- Whose wallet actually moved: credited account on a credit, debited on
    -- anything else. The unused side is stored as '0'.
    CASE WHEN v.VType = 'C' THEN LTRIM(RTRIM(v.CrTo)) ELSE LTRIM(RTRIM(v.DrTo)) END AS AccountId,
    m.MemberName,
    w.Name AS WorkerName
FROM (
    -- Both wallet ledgers in one report: the member's SOLAR wallet and the INC
    -- installer wallet. Separate tables with identical shapes, so a UNION ALL
    -- lists them together without either one owning the other.
    SELECT VoucherId, VoucherNo, VoucherDate, RecTimeStamp, AcType, VType,
           Amount, Narration, RefNo, CrTo, DrTo
    FROM dbo.SolarTrnvoucher
    UNION ALL
    SELECT VoucherId, VoucherNo, VoucherDate, RecTimeStamp, AcType, VType,
           Amount, Narration, RefNo, CrTo, DrTo
    FROM dbo.IncTrnvoucher
) v
LEFT JOIN (
    SELECT Actype, WalletName FROM dbo.SolarVouchertype
    UNION ALL
    SELECT Actype, WalletName FROM dbo.IncVouchertype
) t ON t.Actype = v.AcType
OUTER APPLY (
    SELECT TOP 1 LTRIM(RTRIM(ISNULL(x.MemFirstName, '') + ' ' + ISNULL(x.MemLastName, ''))) AS MemberName
    FROM M_MemberMaster x
    WHERE LTRIM(RTRIM(x.IDNo)) = CASE WHEN v.VType = 'C' THEN LTRIM(RTRIM(v.CrTo)) ELSE LTRIM(RTRIM(v.DrTo)) END
) m
OUTER APPLY (
    SELECT TOP 1 y.Name
    FROM Workers y
    WHERE ISNULL(y.IsDeleted, 0) = 0
      AND CAST(y.Id AS varchar(50)) = CASE WHEN v.VType = 'C' THEN LTRIM(RTRIM(v.CrTo)) ELSE LTRIM(RTRIM(v.DrTo)) END
) w
WHERE (@from   IS NULL OR v.VoucherDate >= @from)
  AND (@to     IS NULL OR v.VoucherDate <  DATEADD(day, 1, @to))
  AND (@actype IS NULL OR v.AcType = @actype)
  AND (@acct   IS NULL OR LTRIM(RTRIM(v.CrTo)) = @acct OR LTRIM(RTRIM(v.DrTo)) = @acct)
ORDER BY v.RecTimeStamp DESC, v.VoucherId DESC;";

    public async Task<List<WalletLedgerRow>> GetAsync(DateTime? from, DateTime? to, string? acType, string? accountId, int top)
    {
        var rows = new List<WalletLedgerRow>();

        var connStr = ConnStr;
        if (string.IsNullOrWhiteSpace(connStr)) return rows;

        await using var conn = new SqlConnection(connStr);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = Sql;
        cmd.Parameters.Add("@top", SqlDbType.Int).Value = Math.Clamp(top, 1, 5000);
        cmd.Parameters.Add("@from", SqlDbType.DateTime).Value = (object?)from?.Date ?? DBNull.Value;
        cmd.Parameters.Add("@to", SqlDbType.DateTime).Value = (object?)to?.Date ?? DBNull.Value;
        cmd.Parameters.Add("@actype", SqlDbType.VarChar, 1).Value =
            string.IsNullOrWhiteSpace(acType) ? DBNull.Value : acType.Trim();
        cmd.Parameters.Add("@acct", SqlDbType.VarChar, 100).Value =
            string.IsNullOrWhiteSpace(accountId) ? DBNull.Value : accountId.Trim();

        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync())
        {
            var member = Str(rd, "MemberName");
            var worker = Str(rd, "WorkerName");
            rows.Add(new WalletLedgerRow
            {
                VoucherId   = Num(rd, "VoucherId") is decimal vid ? (long)vid : 0,
                VoucherNo   = Str(rd, "VoucherNo"),
                VoucherDate = Dt(rd, "VoucherDate"),
                PostedAt    = Dt(rd, "RecTimeStamp"),
                AcType      = Str(rd, "AcType"),
                WalletName  = Str(rd, "WalletName"),
                VType       = Str(rd, "VType"),
                Amount      = Num(rd, "Amount") ?? 0m,
                Remark      = Str(rd, "Narration"),
                RefNo       = Str(rd, "RefNo"),
                AccountId   = Str(rd, "AccountId"),
                // A member row and a worker row can never both match the same id,
                // so whichever came back is the name for this account.
                AccountName = string.IsNullOrWhiteSpace(member) ? worker : member
            });
        }

        return rows;
    }

    public async Task<List<WalletOption>> GetWalletsAsync()
    {
        var list = new List<WalletOption>();

        var connStr = ConnStr;
        if (string.IsNullOrWhiteSpace(connStr)) return list;

        await using var conn = new SqlConnection(connStr);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Acid, WalletName, Actype FROM dbo.SolarVouchertype UNION ALL SELECT Acid, WalletName, Actype FROM dbo.IncVouchertype ORDER BY WalletName";
        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync())
        {
            list.Add(new WalletOption(
                Convert.ToInt32(rd["Acid"]),
                (rd["WalletName"]?.ToString() ?? string.Empty).Trim(),
                (rd["Actype"]?.ToString() ?? string.Empty).Trim()));
        }
        return list;
    }

    // Same defensive readers as the other legacy report: these columns are a mix
    // of numeric / int / varchar across databases migrated more than once.
    private static string? Str(IDataRecord rd, string col)
    {
        var v = rd[col];
        if (v == null || v == DBNull.Value) return null;
        var s = Convert.ToString(v)?.Trim();
        return string.IsNullOrEmpty(s) ? null : s;
    }

    private static decimal? Num(IDataRecord rd, string col)
    {
        var v = rd[col];
        if (v == null || v == DBNull.Value) return null;
        return Convert.ToDecimal(v);
    }

    private static DateTime? Dt(IDataRecord rd, string col)
    {
        var v = rd[col];
        if (v == null || v == DBNull.Value) return null;
        return Convert.ToDateTime(v);
    }
}
