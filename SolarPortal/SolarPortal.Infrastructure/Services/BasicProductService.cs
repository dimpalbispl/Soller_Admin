using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using SolarPortal.Application.Services;
using SolarPortal.Infrastructure.Data;

namespace SolarPortal.Infrastructure.Services;

/// <summary>
/// Reads basic-product rows from the legacy V#SpProductDetail view via raw
/// ADO.NET. We can't use EF here because the view name contains '#' (which
/// SQL Server normally reserves for temp-table names) and isn't part of our
/// EF model. The interface lives in Application; this impl lives in
/// Infrastructure to match the project's layering.
///
/// Pattern matches LiveDbAuthBridge — same raw-SqlConnection approach as the
/// UTR duplicate check in SolarRequestController.
/// </summary>
public class BasicProductService : IBasicProductService
{
    private readonly IConfiguration _config;
    private readonly ApplicationDbContext _db;   // fallback for connection string

    public BasicProductService(IConfiguration config, ApplicationDbContext db)
    {
        _config = config;
        _db = db;
    }

    public async Task<List<BasicProductDto>> GetActiveAsync()
    {
        var rows = new List<BasicProductDto>();
        var connStr = _config.GetConnectionString("DefaultConnection")
                   ?? _db.Database.GetConnectionString();
        if (string.IsNullOrWhiteSpace(connStr)) return rows;

        // The view name contains a `#` character, which is unusual but valid in
        // SQL Server as long as we bracket-quote it. The screenshot showed the
        // view returning these columns; we project only what's needed.
        //
        // SQL Server's # prefix is normally for temp tables — `V#SpProductDetail`
        // works only because it's a CREATED VIEW with that literal name and we
        // wrap it in [brackets]. Do NOT remove the brackets or the parser will
        // try to interpret # as a temp-table prefix.
        const string sql = @"
            SELECT ProdId, ProductName, MRP, DP, BV, CatId, stock
            FROM [V#SpProductDetail]
            WHERE stock > 0   -- hide out-of-stock so user can't pick something unavailable
            ORDER BY ProdId DESC";

        await using var conn = new SqlConnection(connStr);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 30;

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new BasicProductDto
            {
                ProdId      = SafeInt(reader, "ProdId"),
                ProductName = SafeString(reader, "ProductName"),
                MRP         = SafeDecimal(reader, "MRP"),
                DP          = SafeDecimal(reader, "DP"),
                BV          = SafeDecimal(reader, "BV"),
                CatId       = SafeInt(reader, "CatId"),
                Stock       = SafeDecimal(reader, "stock")
            });
        }
        return rows;
    }

    public async Task<BasicProductDto?> GetByIdAsync(int prodId)
    {
        // Server-side single-row lookup. We do this on POST to re-fetch the DP
        // (so a user can't tamper with the hidden form amount) and to validate
        // the chosen ProdId actually exists + is in-stock.
        var connStr = _config.GetConnectionString("DefaultConnection")
                   ?? _db.Database.GetConnectionString();
        if (string.IsNullOrWhiteSpace(connStr)) return null;

        const string sql = @"
            SELECT TOP 1 ProdId, ProductName, MRP, DP, BV, CatId, stock
            FROM [V#SpProductDetail]
            WHERE ProdId = @prodId";

        await using var conn = new SqlConnection(connStr);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.Add(new SqlParameter("@prodId", prodId));

        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;

        return new BasicProductDto
        {
            ProdId      = SafeInt(reader, "ProdId"),
            ProductName = SafeString(reader, "ProductName"),
            MRP         = SafeDecimal(reader, "MRP"),
            DP          = SafeDecimal(reader, "DP"),
            BV          = SafeDecimal(reader, "BV"),
            CatId       = SafeInt(reader, "CatId"),
            Stock       = SafeDecimal(reader, "stock")
        };
    }

    // === Helpers for tolerant column reads ===
    // The legacy view's column types weren't confirmed (decimal vs int vs varchar),
    // so we defensively cast. If a column comes back as DBNull we substitute a
    // sensible default rather than throwing.
    private static string SafeString(SqlDataReader r, string col)
    {
        var i = r.GetOrdinal(col);
        return r.IsDBNull(i) ? string.Empty : r.GetValue(i)?.ToString()?.Trim() ?? string.Empty;
    }
    private static int SafeInt(SqlDataReader r, string col)
    {
        var i = r.GetOrdinal(col);
        if (r.IsDBNull(i)) return 0;
        return Convert.ToInt32(r.GetValue(i));
    }
    private static decimal SafeDecimal(SqlDataReader r, string col)
    {
        var i = r.GetOrdinal(col);
        if (r.IsDBNull(i)) return 0m;
        return Convert.ToDecimal(r.GetValue(i));
    }
}
