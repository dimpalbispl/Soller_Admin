using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using SolarPortal.Application.Interfaces.Services;
using SolarPortal.Domain.Enums;
using SolarPortal.Infrastructure.Data;
using System.Data;

namespace SolarPortal.Infrastructure.Services;

/// <summary>
/// Reads the legacy Repurchincome ledger for the admin report.
///
/// Raw ADO, like every other legacy read in this project: Repurchincome and
/// M_MemberMaster are not in the EF model and migrations must never touch them.
///
/// Two joins turn a ledger row into something readable:
///   * M_MemberMaster on FormNo — the ledger stores no ID number or name.
///   * RemainingBvUpdates on (FormNo, day) — the Remaining BV post stamps its own
///     SessID with the same yyyymmdd value it writes to Repurchincome.DSessid, so
///     that pair identifies the record the BV came from. OUTER APPLY with TOP 1
///     rather than a plain join: a member could have two records posted on one
///     day, and the report must not multiply ledger rows if they do.
/// </summary>
public class RepurchaseIncomeReportService : IRepurchaseIncomeReportService
{
    private readonly IConfiguration _config;
    private readonly ApplicationDbContext _db;   // only for the connection-string fallback

    public RepurchaseIncomeReportService(IConfiguration config, ApplicationDbContext db)
    {
        _config = config;
        _db = db;
    }

    private string? ConnStr => _config.GetConnectionString("DefaultConnection")
                            ?? _db.Database.GetConnectionString();

    private const string Sql = @"
SELECT TOP (@top)
    r.RId,
    r.BillNo,
    r.BillDate,
    r.RecTimeStamp,
    r.FormNo,
    r.RepurchIncome AS Bv,
    r.PVValue       AS Pv,
    r.BillType,
    r.SoldBy,
    r.Remarks,
    r.SessId,
    r.Msessid,
    r.DSessID,
    m.IDNo AS MemberIdNo,
    LTRIM(RTRIM(ISNULL(m.MemFirstName, '') + ' ' + ISNULL(m.MemLastName, ''))) AS MemberName,
    m.Mobl AS Mobile,
    b.RequestNumber,
    b.PlanName,
    b.OrderNo,
    b.SolarTypeKV,
    b.RemainingBV,
    b.SponsorIdNo,
    b.Status AS BvStatus
FROM RepurchIncome r
LEFT JOIN M_MemberMaster m ON m.FormNo = r.FormNo
OUTER APPLY (
    SELECT TOP 1 x.RequestNumber, x.PlanName, x.OrderNo, x.SolarTypeKV,
                 x.RemainingBV, x.SponsorIdNo, x.Status
    FROM RemainingBvUpdates x
    WHERE x.MemberFormNo = r.FormNo
      AND x.SessID       = r.DSessID
      AND ISNULL(x.IsDeleted, 0) = 0
    ORDER BY x.Id DESC
) b
-- Remaining BV posts only. Sp_UpdateReaminingBV hardcodes this pair, and no
-- other flow in this database writes it: everything else is BillType 'A'
-- (activation), 'R', or SoldBy 'WR'.
WHERE r.BillType = 'T' AND r.SoldBy = 'HO'
  AND (@from IS NULL OR r.BillDate >= @from)
  AND (@to   IS NULL OR r.BillDate <  DATEADD(day, 1, @to))
ORDER BY r.RId DESC;";

    public async Task<List<RepurchaseIncomeRow>> GetAsync(DateTime? from, DateTime? to, int top)
    {
        var rows = new List<RepurchaseIncomeRow>();

        var connStr = ConnStr;
        if (string.IsNullOrWhiteSpace(connStr)) return rows;

        await using var conn = new SqlConnection(connStr);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = Sql;
        cmd.Parameters.Add("@top", SqlDbType.Int).Value = Math.Clamp(top, 1, 5000);
        cmd.Parameters.Add("@from", SqlDbType.DateTime).Value = (object?)from?.Date ?? DBNull.Value;
        cmd.Parameters.Add("@to", SqlDbType.DateTime).Value = (object?)to?.Date ?? DBNull.Value;

        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync())
        {
            rows.Add(new RepurchaseIncomeRow
            {
                RId           = Num(rd, "RId") is decimal rid ? (long)rid : 0,
                BillNo        = Str(rd, "BillNo"),
                BillDate      = Dt(rd, "BillDate"),
                PostedAt      = Dt(rd, "RecTimeStamp"),
                FormNo        = Num(rd, "FormNo") ?? 0m,
                Bv            = Num(rd, "Bv") ?? 0m,
                Pv            = Num(rd, "Pv") ?? 0m,
                BillType      = Str(rd, "BillType"),
                SoldBy        = Str(rd, "SoldBy"),
                Remarks       = Str(rd, "Remarks"),
                SessId        = Num(rd, "SessId") ?? 0m,
                MSessId       = (int)(Num(rd, "Msessid") ?? 0m),
                DSessId       = Num(rd, "DSessID") ?? 0m,
                MemberIdNo    = Str(rd, "MemberIdNo"),
                MemberName    = Str(rd, "MemberName"),
                Mobile        = Str(rd, "Mobile"),
                RequestNumber = Str(rd, "RequestNumber"),
                PlanName      = Str(rd, "PlanName"),
                OrderNo       = Str(rd, "OrderNo"),
                SolarTypeKV   = Num(rd, "SolarTypeKV"),
                RemainingBv   = Num(rd, "RemainingBV"),
                SponsorIdNo   = Str(rd, "SponsorIdNo"),
                BvStatus      = Num(rd, "BvStatus") is decimal s ? (ApprovalStatus)(int)s : null
            });
        }

        return rows;
    }

    // The legacy columns are a mix of numeric, int and varchar across databases
    // that were migrated more than once, so every read goes through Convert
    // rather than a typed getter that would throw on the first surprise.
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
