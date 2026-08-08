using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SolarPortal.Application.Interfaces.Services;
using SolarPortal.Domain.Entities;
using SolarPortal.Infrastructure.Data;

namespace SolarPortal.AdminWeb.Areas.SolarPanelAdmin.Controllers;

/// <summary>
/// "admin → user permission &amp; log reports" from the change request.
///
/// Two screens:
///   • Permissions — tick which menus each admin user may open.
///   • Logs        — what every admin actually did, from the activity log the
///                   PM Surya, fund, dispatch and KYC actions write to.
/// </summary>
[Area("SolarPanelAdmin")]
[Authorize(Roles = "Admin,SuperAdmin")]
public class AdminAccessController : Controller
{
    private readonly IAdminPermissionService _permissions;
    private readonly IAdminActivityLogger _activity;
    private readonly ApplicationDbContext _db;
    private readonly UserManager<ApplicationUser> _userManager;

    public AdminAccessController(
        IAdminPermissionService permissions,
        IAdminActivityLogger activity,
        ApplicationDbContext db,
        UserManager<ApplicationUser> userManager)
    {
        _permissions = permissions;
        _activity = activity;
        _db = db;
        _userManager = userManager;
    }

    private string MeId => _userManager.GetUserId(User) ?? "system";

    // ── Permissions ───────────────────────────────────────────────────────
    // GET: /SolarPanelAdmin/AdminAccess/Permissions?user=xyz
    public async Task<IActionResult> Permissions(string? user)
    {
        // Admin accounts live in the legacy m_usermaster; the Identity rows are
        // only shadows created at first login, so the master is the real list.
        // Only LIVE m_usermaster accounts (ActiveStatus = Y or RowStatus = Y).
        // Listing disabled accounts just buries the ones that matter.
        ViewBag.Admins = await _permissions.GetAdminUsersAsync();

        ViewBag.RestrictedUsers = await _permissions.GetRestrictedUsersAsync();
        // GroupId 1 = main admin. Shown so it is obvious WHICH accounts the grid
        // actually governs - restricting a main admin has no effect.
        ViewBag.GroupIds = await _permissions.GetGroupIdsAsync();
        ViewBag.Menus = AdminMenus.All;
        // Used only to stop an admin restricting their own account.
        ViewBag.MyId = MeId;
        ViewBag.SelectedUser = user?.Trim();

        ViewBag.Current = string.IsNullOrWhiteSpace(user)
            ? new List<MenuPermission>()
            : await _permissions.GetAllAsync(user.Trim());

        ViewBag.Title = "User Permissions";
        return View();
    }

    // POST: /SolarPanelAdmin/AdminAccess/SavePermissions
    // The form posts the WHOLE grid; anything absent was un-ticked.
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SavePermissions(string userName, string[]? view, string[]? edit)
    {
        if (string.IsNullOrWhiteSpace(userName))
        {
            TempData["Warning"] = "Pick an admin user first.";
            return RedirectToAction(nameof(Permissions));
        }

        // Refuse to save changes to your own account. The screen hides the grid
        // for it, but a stale tab could still POST - and locking yourself out of
        // the permissions page leaves nobody able to undo it.
        if (string.Equals(userName.Trim(), MeId, StringComparison.OrdinalIgnoreCase))
        {
            TempData["Warning"] = "You cannot change your own access. Ask another admin to do it.";
            return RedirectToAction(nameof(Permissions), new { user = userName.Trim() });
        }

        var viewSet = (view ?? Array.Empty<string>()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var editSet = (edit ?? Array.Empty<string>()).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var payload = AdminMenus.All
            .Select(m => new MenuPermission(m.Key, viewSet.Contains(m.Key), editSet.Contains(m.Key)))
            .ToList();

        await _permissions.SaveAsync(userName.Trim(), payload, MeId);

        await _activity.LogAsync(MeId, "Permission.Save", "AdminUser", userName.Trim(),
            payload.Any(p => p.CanView || p.CanEdit)
                ? $"Set {payload.Count(p => p.CanView)} viewable / {payload.Count(p => p.CanEdit)} editable menu(s)."
                : "Cleared all permissions — this user is back to unrestricted access.",
            HttpContext.Connection.RemoteIpAddress?.ToString());

        TempData["Success"] = payload.Any(p => p.CanView || p.CanEdit)
            ? $"Permissions saved for {userName}."
            : $"All permissions cleared for {userName} — they now have unrestricted access again.";

        return RedirectToAction(nameof(Permissions), new { user = userName.Trim() });
    }

    // ── Log report ────────────────────────────────────────────────────────
    // GET: /SolarPanelAdmin/AdminAccess/Logs
    public async Task<IActionResult> Logs(string? adminUser, string? action, DateTime? from, DateTime? to)
    {
        // `to` arrives as a date; include the whole day rather than stopping at
        // midnight, otherwise today's entries never show up.
        var toUtc = to?.Date.AddDays(1).AddTicks(-1);

        var rows = await _activity.SearchAsync(adminUser, action, from?.Date, toUtc);

        ViewBag.AdminUsers = await _db.AdminUsers.AsNoTracking()
            .Where(u => u.UserName != null)
            .OrderBy(u => u.UserName)
            .Select(u => u.UserName!)
            .ToListAsync();

        // Action prefixes actually present, so the filter offers real options
        // rather than a hardcoded list that rots as features are added.
        ViewBag.Actions = await _db.ActivityLogs.AsNoTracking()
            .Select(l => l.Action)
            .Distinct()
            .OrderBy(a => a)
            .ToListAsync();

        ViewBag.GroupIds = await _permissions.GetGroupIdsAsync();
        ViewBag.FilterUser = adminUser;
        ViewBag.FilterAction = action;
        ViewBag.FilterFrom = from;
        ViewBag.FilterTo = to;
        ViewBag.Title = "Log Report";
        return View(rows);
    }

    public record AdminRow(string UserName, string? Email, bool IsActive);
}
