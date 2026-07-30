using SolarPortal.Application.DTOs;

namespace SolarPortal.Application.Services;

/// <summary>
/// Builds the Activation History report — one entry per member showing the
/// order in which they used each request mode and when their MLM ID actually
/// went active.
///
/// Per spec (Hinglish): "without activation liya, fir payment complete karke
/// id active ki, ab Active Now se project le raha hu — kab kya liya uski alag
/// report honi chahiye."
///
/// Three sources are stitched together:
///   1. SolarRequests  — which mode was used and when (portal DB)
///   2. Payments       — submitted / verified / rejected money trail (portal DB)
///   3. TrnProductorderDetail (ForType='A') joined to M_MemberMaster —
///      the legacy MLM activation order and its Approvedate (live DB)
///
/// The legacy leg is best-effort: if that DB/table is unavailable the report
/// still renders from portal data alone, with activation dates marked
/// approximate.
/// </summary>
public interface IActivationHistoryService
{
    /// <summary>
    /// Every member who has at least one real (non-stub) solar request,
    /// newest activity first.
    /// </summary>
    Task<IReadOnlyList<MemberActivationHistoryDto>> GetAllAsync();

    /// <summary>One member's history, or null when the member has no real requests.</summary>
    Task<MemberActivationHistoryDto?> GetByMemberIdAsync(string memberIdNo);
}
