namespace SolarPortal.Domain.Entities;

/// <summary>
/// What one admin user is allowed to see and do, per menu.
///
/// Keyed by m_usermaster.UserName rather than an Identity Id: admin accounts
/// live in the legacy table and their shadow Identity rows are created lazily
/// by the login bridge, so the username is the only id that always exists.
///
/// A missing row means "not configured", NOT "denied" — an admin with no rows
/// at all keeps full access, so switching this feature on does not lock anyone
/// out of a panel they were already using.
/// </summary>
public class AdminPermission
{
    public int Id { get; set; }

    public string UserName { get; set; } = string.Empty;

    /// <summary>Stable key of the menu item, e.g. "Operations.MaterialDispatch".</summary>
    public string MenuKey { get; set; } = string.Empty;

    public bool CanView { get; set; } = true;
    public bool CanEdit { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public string? UpdatedBy { get; set; }
}
