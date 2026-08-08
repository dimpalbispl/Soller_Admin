namespace SolarPortal.Application.Interfaces.Services;

/// <summary>
/// Which admin can open which menu ("admin → user permission").
///
/// The rule that keeps this safe to switch on: an admin with NO rows at all has
/// full access. Permissions are opt-in per user, so adding the feature cannot
/// lock out anyone who was already working. Once a user has at least one row,
/// only what is ticked is allowed.
/// </summary>
public interface IAdminPermissionService
{
    /// <summary>Menu keys this user may open. Null means "unrestricted" (no rows configured).</summary>
    Task<HashSet<string>?> GetViewableAsync(string userName);

    /// <summary>Menu keys this user may act on (not just read). Null means unrestricted.</summary>
    Task<HashSet<string>?> GetEditableAsync(string userName);

    Task<bool> CanViewAsync(string userName, string menuKey);

    /// <summary>
    /// m_usermaster.GroupId = 1 is the MAIN admin. Everything is visible to them
    /// and the menu grid does not apply - the grid exists to scope the GroupId
    /// &lt;&gt; 1 staff accounts, which is who Add Fund / Approve Fund / Log Report
    /// were separated for in the first place.
    ///
    /// Note this controls VISIBILITY only. It deliberately does not lift the
    /// maker-checker rule on funds or the PM Surya accept-lock: a main admin still
    /// cannot approve money they added themselves, and still has to accept a
    /// PM Surya case before deciding it.
    /// </summary>
    Task<bool> IsMainAdminAsync(string userName);

    /// <summary>
    /// Admin accounts the permission screen can govern: m_usermaster rows that are
    /// LIVE (ActiveStatus = 'Y' or RowStatus = 'Y'). Disabled and deleted accounts
    /// are left out - granting menus to an account nobody can sign in to is noise
    /// that makes the real list hard to read.
    /// </summary>
    Task<List<AdminUserRow>> GetAdminUsersAsync();

    /// <summary>userName -> m_usermaster.GroupId, for the permissions screen.</summary>
    Task<Dictionary<string, int?>> GetGroupIdsAsync();

    /// <summary>Replaces the whole grid for one user. An empty list restores unrestricted access.</summary>
    Task SaveAsync(string userName, IEnumerable<MenuPermission> permissions, string updatedBy);

    /// <summary>Everything currently configured for one user, for the edit screen.</summary>
    Task<IReadOnlyList<MenuPermission>> GetAllAsync(string userName);

    /// <summary>Usernames that have at least one permission row — i.e. are restricted.</summary>
    Task<HashSet<string>> GetRestrictedUsersAsync();
}

public record MenuPermission(string MenuKey, bool CanView, bool CanEdit);

/// <summary>
/// The admin menu, as permission keys. One entry per sidebar item so the
/// permission screen and the sidebar can never drift apart — both read this.
/// </summary>
public static class AdminMenus
{
    public record Item(string Key, string Label, string Group, string Controller, string Action);

    public static readonly IReadOnlyList<Item> All = new[]
    {
        new Item("Dashboard",                 "Dashboard",            "Admin Menu",       "Dashboard",         "Index"),
        new Item("Projects",                  "All Projects",         "Admin Menu",       "Projects",          "Index"),

        new Item("Payments",                  "Payment Verification", "Users & Payments", "Payments",          "Index"),
        new Item("Funds.Add",                 "Add Fund",             "Users & Payments", "Funds",             "Index"),
        new Item("Funds.Approve",             "Approve Fund",         "Users & Payments", "Funds",             "Approve"),

        new Item("ActivationHistory",         "Activation History",   "Reports",          "ActivationHistory", "Index"),
        new Item("AdminAccess.Logs",          "Log Report",           "Reports",          "AdminAccess",       "Logs"),

        new Item("PMSurya",                   "PM Surya Ghar",        "Workflow",         "PMSurya",           "Index"),
        new Item("Operations.MeterDispatch",  "Meter Dispatch",       "Workflow",         "Operations",        "MeterDispatch"),
        new Item("SiteSurvey",                "Site Survey",          "Workflow",         "SiteSurvey",        "Index"),
        new Item("Operations.PrepareDispatch","Prepare for Dispatch", "Workflow",         "Operations",        "PrepareDispatch"),
        new Item("Operations.FinalDispatch",  "Final Dispatch",       "Workflow",         "Operations",        "FinalDispatch"),
        new Item("Operations.Installation",   "Installation",         "Workflow",         "Operations",        "Installation"),
        new Item("Operations.InstallationApprovals", "Installation Approval", "Workflow",     "Operations",        "InstallationApprovals"),
        new Item("Operations.DCRUpdate",      "DCR Verify",           "Workflow",         "Operations",        "DCRUpdate"),

        new Item("SolarProjects",             "Solar Projects",       "Masters",          "SolarProjects",     "Index"),
        new Item("Workers",                   "Installers (INC)",     "Masters",          "Workers",           "Index"),

        new Item("Commission",                "INC Commission",       "Management",       "Commission",        "Index"),
        new Item("Inc.Connections",           "INC Connections",      "Management",       "Inc",               "Connections"),
        new Item("Inc.Kyc",                   "INC KYC",              "Management",       "Inc",               "Kyc"),
        new Item("Inc.Withdrawals",           "INC Withdrawals",      "Management",       "Inc",               "Withdrawals"),
        new Item("AdminAccess.Permissions",   "User Permissions",     "Management",       "AdminAccess",       "Permissions"),
    };

    /// <summary>
    /// Permission key for a controller/action pair, or null when the target is
    /// not a menu (detail pages, POST handlers). Detail pages fall back to their
    /// controller's Index key so a user denied a menu cannot deep-link into it.
    /// </summary>
    public static string? KeyFor(string controller, string action)
    {
        var exact = All.FirstOrDefault(m =>
            string.Equals(m.Controller, controller, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(m.Action, action, StringComparison.OrdinalIgnoreCase));
        if (exact != null) return exact.Key;

        // Same controller, only one menu → everything in it belongs to that menu.
        var byController = All.Where(m =>
            string.Equals(m.Controller, controller, StringComparison.OrdinalIgnoreCase)).ToList();
        return byController.Count == 1 ? byController[0].Key : null;
    }
}

/// <summary>One selectable admin account on the permission screen.</summary>
public record AdminUserRow(string UserName, string? Email, int? GroupId, bool IsActive);