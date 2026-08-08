using SolarPortal.Domain.Entities;

namespace SolarPortal.Application.Interfaces.Services;

public interface ILiveDbAuthBridge
{
    /// <summary>
    /// Try to authenticate against m_membermaster.
    /// Returns the ApplicationUser (loaded via raw SQL, bypassing UserManager)
    /// if successful, null otherwise.
    /// </summary>
    Task<ApplicationUser?> TryBridgeUserAsync(string idNo, string password);

    /// <summary>
    /// Try to authenticate against m_usermaster.
    /// Returns the ApplicationUser (loaded via raw SQL) if successful, null otherwise.
    /// </summary>
    Task<ApplicationUser?> TryBridgeAdminAsync(string userName, string password);

    /// <summary>
    /// Loads the shadow Identity user for an admin WITHOUT re-checking the
    /// password. Only for the second leg of OTP sign-in, where the password was
    /// already verified by <see cref="TryBridgeAdminAsync"/> moments earlier and
    /// deliberately not kept around. Returns null when no shadow user exists,
    /// which means the first leg never ran.
    /// </summary>
    Task<ApplicationUser?> LoadBridgedAdminAsync(string userName);

    /// <summary>
    /// Does an admin with this User ID exist on m_usermaster? Used by step 1 of
    /// the sign-in wizard so a mistyped User ID is caught there instead of
    /// resurfacing as "wrong password" a step later. Checks existence ONLY - it
    /// never looks at, and can never accept, a password.
    /// </summary>
    Task<bool> AdminExistsAsync(string userName);
}
