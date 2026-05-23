using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using SolarPortal.Application.Services;
using SolarPortal.Infrastructure.Data;

namespace SolarPortal.Infrastructure.Services;

/// <summary>
/// Reads active payment modes from the legacy M_PayModeMaster table via raw
/// ADO.NET (same pattern as BasicProductService / LegacyProductRequestService).
///
/// Mirrors the original VB query:
///     SELECT * FROM M_PayModeMaster WHERE ActiveStatus='Y' ORDER BY Pid
///
/// If the table or columns aren't reachable, returns a small built-in fallback
/// list so the payment form never ends up with an empty dropdown.
/// </summary>
public class PayModeService : IPayModeService
{
    private readonly IConfiguration _config;
    private readonly ApplicationDbContext _db;

    public PayModeService(IConfiguration config, ApplicationDbContext db)
    {
        _config = config;
        _db = db;
    }

    public async Task<List<PayModeDto>> GetActiveAsync()
    {
        var rows = new List<PayModeDto>();
        var connStr = _config.GetConnectionString("DefaultConnection")
                   ?? _db.Database.GetConnectionString();
        if (string.IsNullOrWhiteSpace(connStr))
            return Fallback();

        try
        {
            await using var conn = new SqlConnection(connStr);
            await conn.OpenAsync();

            // Column names match the actual M_PayModeMaster schema exactly:
            //   PId, PayMode, ActiveStatus, IsTransNo, TransNoLbl
            // (SQL Server is case-insensitive for identifiers by default, but we
            //  use the real casing to be safe.) IsTransNo / TransNoLbl tell the
            //  UI whether a transaction-number field applies for this mode.
            const string sql = @"
                SELECT PId, PayMode, ISNULL(IsTransNo, 'N') AS IsTransNo,
                       ISNULL(TransNoLbl, '') AS TransNoLbl
                FROM M_PayModeMaster
                WHERE ActiveStatus = 'Y'
                ORDER BY PId";

            await using var cmd = new SqlCommand(sql, conn);
            await using var rdr = await cmd.ExecuteReaderAsync();
            while (await rdr.ReadAsync())
            {
                var name = rdr["PayMode"]?.ToString()?.Trim() ?? "";

                // The legacy table seeds a placeholder row like "--Choose Payment
                // Mode--" that the old UI used as the dropdown's default prompt.
                // Our views already render their own "Select method" prompt, so we
                // skip these placeholder rows to avoid a double prompt. We treat a
                // row as a placeholder if it's empty, starts with punctuation, or
                // reads like "choose"/"select".
                if (string.IsNullOrWhiteSpace(name)) continue;
                var lower = name.ToLowerInvariant();
                if (lower.StartsWith("--") || lower.StartsWith("- ") ||
                    lower.Contains("choose") || lower.Contains("select"))
                    continue;

                rows.Add(new PayModeDto
                {
                    Pid             = Convert.ToInt32(rdr["PId"]),
                    Paymode         = name,
                    RequiresTransNo = string.Equals(rdr["IsTransNo"]?.ToString()?.Trim(),
                                                    "Y", StringComparison.OrdinalIgnoreCase),
                    TransNoLabel    = rdr["TransNoLbl"]?.ToString()?.Trim() ?? ""
                });
            }
        }
        catch
        {
            // Table unreachable / column mismatch — fall back so the form works.
            return Fallback();
        }

        return rows.Count > 0 ? rows : Fallback();
    }

    // Built-in list used only if the legacy table can't be read. Keeps the
    // payment form usable in dev / offline scenarios.
    private static List<PayModeDto> Fallback() => new()
    {
        new() { Pid = 1, Paymode = "UPI",            RequiresTransNo = true  },
        new() { Pid = 2, Paymode = "Bank Transfer",  RequiresTransNo = true  },
        new() { Pid = 3, Paymode = "Cash",           RequiresTransNo = false },
        new() { Pid = 4, Paymode = "Card Payment",   RequiresTransNo = true  },
        new() { Pid = 5, Paymode = "Cheque",         RequiresTransNo = true  },
        new() { Pid = 6, Paymode = "Demand Draft (DD)", RequiresTransNo = true },
    };
}
