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
}
