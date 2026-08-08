using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SolarPortal.Application.Interfaces.Services;
using SolarPortal.Domain.Entities;
using SolarPortal.Domain.Enums;
using SolarPortal.Infrastructure.Data;

namespace SolarPortal.Infrastructure.Services;

/// <inheritdoc />
/// <remarks>
/// The query is a copy of the user panel's LegacyProductRequestService lookup and
/// must stay identical — the two panels have to agree on how much a member has on
/// deposit, or the same project shows a different balance in each.
///
/// Raw SQL because TrnProductorderDetail / M_MemberMaster are legacy cPanel tables
/// that are not in this app's EF model.
/// </remarks>
public class ActiveIdDepositService : IActiveIdDepositService
{
    private readonly IConfiguration _config;
    private readonly ApplicationDbContext _db;
    private readonly ILogger<ActiveIdDepositService> _log;

    public ActiveIdDepositService(IConfiguration config, ApplicationDbContext db,
        ILogger<ActiveIdDepositService> log)
    {
        _config = config;
        _db = db;
        _log = log;
    }

    private string? ConnStr => _config.GetConnectionString("DefaultConnection")
                            ?? _db.Database.GetConnectionString();

    public async Task<decimal> GetForMemberAsync(string memberIdNo)
    {
        if (string.IsNullOrWhiteSpace(memberIdNo)) return 0m;

        var connStr = ConnStr;
        if (string.IsNullOrWhiteSpace(connStr)) return 0m;

        try
        {
            await using var conn = new SqlConnection(connStr);
            await conn.OpenAsync();

            // Order rows key on Formno, not Idno.
            await using var cmd = new SqlCommand(@"
SELECT ISNULL(SUM(d.NetAmount), 0)
FROM TrnProductorderDetail d
WHERE d.FormNo = (SELECT TOP 1 Formno FROM M_MemberMaster WHERE Idno = @id)
  AND d.IsApprove = 'Y';", conn);
            cmd.Parameters.AddWithValue("@id", memberIdNo.Trim());

            var v = await cmd.ExecuteScalarAsync();
            if (v == null || v == DBNull.Value) return 0m;

            var amt = Convert.ToDecimal(v);
            return amt > 0m ? amt : 0m;
        }
        catch (Exception ex)
        {
            // A legacy-DB problem must not blank out the Payments page. Reporting 0
            // shows the un-adjusted due, which is the pre-existing behaviour.
            _log.LogWarning(ex, "Legacy deposit lookup failed for IdNo '{IdNo}'.", memberIdNo);
            return 0m;
        }
    }

    public async Task<Dictionary<int, decimal>> GetForRequestsAsync(IEnumerable<SolarRequest> requests)
    {
        var map = new Dictionary<int, decimal>();

        // One lookup per distinct member, not per request — a member with several
        // requests has ONE deposit, and it must not be counted once per row.
        var byMember = requests
            .Where(r => r.RequestType == RequestType.AlreadyActiveOnlyRequest
                     && !string.IsNullOrWhiteSpace(r.UserId))
            .GroupBy(r => r.UserId.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var grp in byMember)
        {
            var amount = await GetForMemberAsync(grp.Key);
            if (amount <= 0m) continue;

            // If one member somehow has more than one Already-Active request, the
            // deposit belongs to the earliest — crediting it to every one of them
            // would invent money.
            var owner = grp.OrderBy(r => r.CreatedAt).First();
            map[owner.Id] = amount;
        }

        return map;
    }
}
