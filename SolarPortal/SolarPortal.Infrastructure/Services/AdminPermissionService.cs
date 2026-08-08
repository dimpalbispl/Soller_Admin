using Microsoft.EntityFrameworkCore;
using SolarPortal.Application.Interfaces.Services;
using SolarPortal.Domain.Entities;
using SolarPortal.Infrastructure.Data;

namespace SolarPortal.Infrastructure.Services;

/// <inheritdoc />
public class AdminPermissionService : IAdminPermissionService
{
    private readonly ApplicationDbContext _db;

    public AdminPermissionService(ApplicationDbContext db) => _db = db;

    public async Task<HashSet<string>?> GetViewableAsync(string userName)
    {
        var rows = await RowsAsync(userName);
        // No rows at all → unrestricted. Signalled with null so a caller can tell
        // "not configured" apart from "configured, but allowed nothing".
        if (rows.Count == 0) return null;

        return rows.Where(r => r.CanView).Select(r => r.MenuKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public async Task<HashSet<string>?> GetEditableAsync(string userName)
    {
        var rows = await RowsAsync(userName);
        if (rows.Count == 0) return null;

        return rows.Where(r => r.CanEdit).Select(r => r.MenuKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public async Task<bool> CanViewAsync(string userName, string menuKey)
    {
        var viewable = await GetViewableAsync(userName);
        return viewable == null || viewable.Contains(menuKey);
    }

    public async Task SaveAsync(string userName, IEnumerable<MenuPermission> permissions, string updatedBy)
    {
        var name = (userName ?? string.Empty).Trim();
        if (name.Length == 0) return;

        // Replace wholesale: the screen posts the complete grid, so anything not
        // in the payload was un-ticked and must not survive as a stale row.
        var existing = await _db.AdminPermissions.Where(p => p.UserName == name).ToListAsync();
        _db.AdminPermissions.RemoveRange(existing);

        foreach (var p in permissions.Where(p => p.CanView || p.CanEdit))
        {
            _db.AdminPermissions.Add(new AdminPermission
            {
                UserName = name,
                MenuKey = p.MenuKey,
                CanView = p.CanView || p.CanEdit,   // acting on a menu implies seeing it
                CanEdit = p.CanEdit,
                UpdatedAt = DateTime.UtcNow,
                UpdatedBy = updatedBy
            });
        }

        await _db.SaveChangesAsync();
    }

    public async Task<IReadOnlyList<MenuPermission>> GetAllAsync(string userName) =>
        (await RowsAsync(userName))
        .Select(r => new MenuPermission(r.MenuKey, r.CanView, r.CanEdit))
        .ToList();

    public async Task<HashSet<string>> GetRestrictedUsersAsync()
    {
        try
        {
            return (await _db.AdminPermissions.AsNoTracking()
                             .Select(p => p.UserName).Distinct().ToListAsync())
                   .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>GroupId of the MAIN admin on m_usermaster.</summary>
    private const int MainAdminGroupId = 1;

    public async Task<List<AdminUserRow>> GetAdminUsersAsync()
    {
        // Raw SQL, and RowStatus is tried FIRST but not depended on: it is a
        // legacy convention on these tables (M_StateDivMaster uses it) and may or
        // may not exist on m_usermaster. If the column is missing the query throws
        // "Invalid column name", so fall back to ActiveStatus alone rather than
        // letting the whole permissions screen die over it.
        const string withRowStatus = @"
SELECT UserName, Email, GroupId, ActiveStatus
  FROM m_usermaster
 WHERE UserName IS NOT NULL
   AND (LTRIM(RTRIM(ISNULL(ActiveStatus,''))) = 'Y'
     OR LTRIM(RTRIM(ISNULL(RowStatus,'')))    = 'Y')
 ORDER BY UserName";

        const string activeOnly = @"
SELECT UserName, Email, GroupId, ActiveStatus
  FROM m_usermaster
 WHERE UserName IS NOT NULL
   AND LTRIM(RTRIM(ISNULL(ActiveStatus,''))) = 'Y'
 ORDER BY UserName";

        try { return await ReadAdminsAsync(withRowStatus); }
        catch
        {
            try { return await ReadAdminsAsync(activeOnly); }
            catch { return new List<AdminUserRow>(); }
        }
    }

    private async Task<List<AdminUserRow>> ReadAdminsAsync(string sql)
    {
        var rows = new List<AdminUserRow>();
        var conn = _db.Database.GetDbConnection();
        var opened = false;
        if (conn.State != System.Data.ConnectionState.Open) { await conn.OpenAsync(); opened = true; }
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            await using var rd = await cmd.ExecuteReaderAsync();
            while (await rd.ReadAsync())
            {
                var name = rd.IsDBNull(0) ? null : rd.GetValue(0)?.ToString()?.Trim();
                if (string.IsNullOrWhiteSpace(name)) continue;

                var email = rd.IsDBNull(1) ? null : rd.GetValue(1)?.ToString()?.Trim();
                int? gid = rd.IsDBNull(2) ? null : Convert.ToInt32(rd.GetValue(2));
                var act = rd.IsDBNull(3) ? "" : (rd.GetValue(3)?.ToString() ?? "").Trim();

                rows.Add(new AdminUserRow(name, email, gid,
                    string.Equals(act, "Y", StringComparison.OrdinalIgnoreCase)));
            }
        }
        finally { if (opened) await conn.CloseAsync(); }
        return rows;
    }


    public async Task<bool> IsMainAdminAsync(string userName)
    {
        var name = (userName ?? string.Empty).Trim();
        if (name.Length == 0) return false;
        try
        {
            var gid = await _db.AdminUsers.AsNoTracking()
                .Where(u => u.UserName != null && u.UserName.Trim() == name)
                .Select(u => u.GroupId)
                .FirstOrDefaultAsync();

            return gid.HasValue && (int)gid.Value == MainAdminGroupId;
        }
        catch
        {
            // A legacy-DB hiccup must not silently PROMOTE someone to main admin.
            // Reporting false just means the normal permission grid applies.
            return false;
        }
    }

    public async Task<Dictionary<string, int?>> GetGroupIdsAsync()
    {
        try
        {
            var rows = await _db.AdminUsers.AsNoTracking()
                .Where(u => u.UserName != null)
                .Select(u => new { u.UserName, u.GroupId })
                .ToListAsync();

            return rows
                .GroupBy(r => r.UserName!.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key,
                              g => g.First().GroupId.HasValue ? (int?)(int)g.First().GroupId!.Value : null,
                              StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase);
        }
    }
    private async Task<List<AdminPermission>> RowsAsync(string userName)
    {
        var name = (userName ?? string.Empty).Trim();
        if (name.Length == 0) return new List<AdminPermission>();

        try
        {
            return await _db.AdminPermissions.AsNoTracking()
                            .Where(p => p.UserName == name)
                            .ToListAsync();
        }
        catch
        {
            // The sidebar reads permissions on EVERY admin page. If the table is
            // not there yet (the schema script has not been run), an exception
            // here would take down the whole panel. Falling back to "no rows"
            // means unrestricted — the same as before this feature existed.
            return new List<AdminPermission>();
        }
    }
}
