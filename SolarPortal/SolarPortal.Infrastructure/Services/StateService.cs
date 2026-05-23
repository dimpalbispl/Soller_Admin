using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using SolarPortal.Application.Services;
using SolarPortal.Infrastructure.Data;

namespace SolarPortal.Infrastructure.Services;

/// <summary>
/// Reads active states from the legacy M_StateDivMaster table via raw ADO.NET
/// (same pattern as PayModeService / BasicProductService).
///
/// Mirrors the original VB Fill_State():
///     SELECT StateCode, StateName FROM M_StateDivMaster
///     WHERE ActiveStatus='Y' AND RowStatus='Y' ORDER BY StateName
///
/// Falls back to a small built-in list if the table can't be reached so the
/// State dropdown is never empty.
/// </summary>
public class StateService : IStateService
{
    private readonly IConfiguration _config;
    private readonly ApplicationDbContext _db;

    public StateService(IConfiguration config, ApplicationDbContext db)
    {
        _config = config;
        _db = db;
    }

    public async Task<List<StateDto>> GetActiveAsync()
    {
        var rows = new List<StateDto>();
        var connStr = _config.GetConnectionString("DefaultConnection")
                   ?? _db.Database.GetConnectionString();
        if (string.IsNullOrWhiteSpace(connStr))
            return Fallback();

        try
        {
            await using var conn = new SqlConnection(connStr);
            await conn.OpenAsync();

            const string sql = @"
                SELECT StateCode, StateName
                FROM M_StateDivMaster
                WHERE ActiveStatus = 'Y' AND RowStatus = 'Y'
                ORDER BY StateName";

            await using var cmd = new SqlCommand(sql, conn);
            await using var rdr = await cmd.ExecuteReaderAsync();
            while (await rdr.ReadAsync())
            {
                var name = rdr["StateName"]?.ToString()?.Trim() ?? "";

                // Skip placeholder/header rows that exist in the legacy table
                // (e.g. "--Choose State Name--", "-- Select --"). These are not
                // real states and would show up as a duplicate "choose" option
                // on top of our own "Select" placeholder.
                if (string.IsNullOrWhiteSpace(name)) continue;
                var lower = name.ToLowerInvariant();
                if (name.StartsWith("--") ||
                    lower.Contains("choose") ||
                    lower.Contains("select"))
                    continue;

                rows.Add(new StateDto
                {
                    StateCode = rdr["StateCode"]?.ToString()?.Trim() ?? "",
                    StateName = name
                });
            }
        }
        catch
        {
            return Fallback();
        }

        return rows.Count > 0 ? rows : Fallback();
    }

    // Built-in list used only if the legacy table can't be read.
    private static List<StateDto> Fallback() => new()
    {
        new() { StateCode = "RJ", StateName = "Rajasthan" },
        new() { StateCode = "GJ", StateName = "Gujarat" },
        new() { StateCode = "MH", StateName = "Maharashtra" },
        new() { StateCode = "UP", StateName = "Uttar Pradesh" },
        new() { StateCode = "MP", StateName = "Madhya Pradesh" },
        new() { StateCode = "DL", StateName = "Delhi" },
    };
}
