using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using SolarPortal.Application.Services;
using SolarPortal.Domain.Enums;
using SolarPortal.Infrastructure.Data;

namespace SolarPortal.Infrastructure.Services;

/// <summary>
/// Sponsor-tree reads against the legacy m_membermaster table via raw ADO.NET -
/// same pattern as BasicProductService / LegacyProductRequestService.
///
/// Why raw SQL and not EF: the sponsor tree is walked with a recursive CTE, which
/// EF Core cannot express, and MMemberMaster is mapped read-only without the
/// FormNo / RefFormNo columns this needs.
/// </summary>
public class SponsorTreeService : ISponsorTreeService
{
    /// <summary>
    /// Hard stop on the recursive walk. Real sponsor chains are a few dozen deep
    /// at most; the guard exists so a cycle in legacy data cannot spin forever
    /// (the query runs with MAXRECURSION 0, so SQL Server will not stop it for us).
    /// </summary>
    private const int MaxChainDepth = 200;

    private readonly IConfiguration _config;
    private readonly ApplicationDbContext _db;   // fallback for connection string

    public SponsorTreeService(IConfiguration config, ApplicationDbContext db)
    {
        _config = config;
        _db = db;
    }

    private string? ConnStr =>
        _config.GetConnectionString("DefaultConnection") ?? _db.Database.GetConnectionString();

    public async Task<SponsorMemberDto?> GetMemberAsync(string idNo)
    {
        var id = (idNo ?? string.Empty).Trim();
        if (id.Length == 0) return null;

        var connStr = ConnStr;
        if (string.IsNullOrWhiteSpace(connStr)) return null;

        const string sql = @"
            SELECT TOP 1
                   m.FormNo,
                   LTRIM(RTRIM(m.IdNo))                                  AS IdNo,
                   LTRIM(RTRIM(ISNULL(m.MemFirstName,'') + ' ' + ISNULL(m.MemLastName,''))) AS MemName,
                   m.ActiveStatus,
                   LTRIM(RTRIM(ISNULL(s.IdNo,'')))                       AS SponsorIdNo,
                   LTRIM(RTRIM(ISNULL(s.MemFirstName,'') + ' ' + ISNULL(s.MemLastName,''))) AS SponsorName
            FROM   m_membermaster m
            LEFT   JOIN m_membermaster s ON s.FormNo = m.RefFormNo
            WHERE  LTRIM(RTRIM(m.IdNo)) = @idNo";

        await using var conn = new SqlConnection(connStr);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 30;
        cmd.Parameters.Add(new SqlParameter("@idNo", id));

        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;

        var sponsorId = Str(reader, "SponsorIdNo");
        return new SponsorMemberDto
        {
            FormNo      = Dec(reader, "FormNo"),
            IdNo        = Str(reader, "IdNo"),
            FullName    = Str(reader, "MemName"),
            SponsorIdNo = sponsorId.Length == 0 ? null : sponsorId,
            SponsorName = sponsorId.Length == 0 ? null : Str(reader, "SponsorName"),
            IsActive    = string.Equals(Str(reader, "ActiveStatus"), "Y", StringComparison.OrdinalIgnoreCase)
        };
    }

    public async Task<SponsorRelationResult> CheckRelationAsync(string memberIdNo, string candidateIdNo)
    {
        var member = (memberIdNo ?? string.Empty).Trim();
        var candidate = (candidateIdNo ?? string.Empty).Trim();

        if (member.Length == 0 || candidate.Length == 0)
            return new SponsorRelationResult { Relation = SponsorRelation.NotFound };

        if (string.Equals(member, candidate, StringComparison.OrdinalIgnoreCase))
            return new SponsorRelationResult { Relation = SponsorRelation.Self, IdNo = candidate };

        // The candidate has to be a real member before we bother walking the tree.
        var candidateRow = await GetMemberAsync(candidate);
        if (candidateRow == null)
            return new SponsorRelationResult { Relation = SponsorRelation.NotFound };

        var result = new SponsorRelationResult
        {
            IdNo = candidateRow.IdNo,
            Name = candidateRow.FullName
        };

        // Walk UP from the member: is the candidate one of their sponsors?
        var upLevel = await AncestorLevelAsync(descendantIdNo: member, ancestorIdNo: candidate);
        if (upLevel > 0)
        {
            result.Relation = SponsorRelation.Upline;
            result.Level = upLevel;
            return result;
        }

        // The same walk with the roles swapped tells us the candidate is BELOW the
        // member, so the message can give the real reason instead of a flat "no".
        var downLevel = await AncestorLevelAsync(descendantIdNo: candidate, ancestorIdNo: member);
        result.Relation = downLevel > 0 ? SponsorRelation.Downline : SponsorRelation.Unrelated;
        return result;
    }

    /// <summary>
    /// How many sponsor hops above <paramref name="descendantIdNo"/> the member
    /// <paramref name="ancestorIdNo"/> sits - 1 = direct sponsor. Returns 0 when
    /// the ancestor is not on that chain at all.
    /// </summary>
    private async Task<int> AncestorLevelAsync(string descendantIdNo, string ancestorIdNo)
    {
        var connStr = ConnStr;
        if (string.IsNullOrWhiteSpace(connStr)) return 0;

        // Anchor = the member itself at level 0; each recursive step climbs one
        // sponsor (RefFormNo). Level 0 is excluded from the match so "self" can
        // never be reported as upline. MAXRECURSION 0 lifts SQL Server's default
        // 100-level cap - the @maxDepth guard is what actually bounds the walk.
        const string sql = @"
            ;WITH chain AS
            (
                SELECT m.FormNo, m.RefFormNo, 0 AS Lvl
                FROM   m_membermaster m
                WHERE  LTRIM(RTRIM(m.IdNo)) = @descendant

                UNION ALL

                SELECT p.FormNo, p.RefFormNo, c.Lvl + 1
                FROM   m_membermaster p
                INNER  JOIN chain c ON p.FormNo = c.RefFormNo
                WHERE  c.Lvl < @maxDepth
            )
            SELECT TOP 1 c.Lvl
            FROM   chain c
            INNER  JOIN m_membermaster a ON a.FormNo = c.FormNo
            WHERE  c.Lvl > 0
              AND  LTRIM(RTRIM(a.IdNo)) = @ancestor
            ORDER  BY c.Lvl
            OPTION (MAXRECURSION 0)";

        await using var conn = new SqlConnection(connStr);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 60;
        cmd.Parameters.Add(new SqlParameter("@descendant", descendantIdNo));
        cmd.Parameters.Add(new SqlParameter("@ancestor", ancestorIdNo));
        cmd.Parameters.Add(new SqlParameter("@maxDepth", MaxChainDepth));

        var scalar = await cmd.ExecuteScalarAsync();
        if (scalar == null || scalar == DBNull.Value) return 0;
        return Convert.ToInt32(scalar);
    }

    // === helpers ===
    private static string Str(SqlDataReader r, string col)
    {
        var i = r.GetOrdinal(col);
        return r.IsDBNull(i) ? string.Empty : r.GetValue(i)?.ToString()?.Trim() ?? string.Empty;
    }

    private static decimal Dec(SqlDataReader r, string col)
    {
        var i = r.GetOrdinal(col);
        return r.IsDBNull(i) ? 0m : Convert.ToDecimal(r.GetValue(i));
    }
}
