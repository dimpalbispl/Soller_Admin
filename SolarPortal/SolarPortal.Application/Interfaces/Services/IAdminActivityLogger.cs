using SolarPortal.Domain.Entities;

namespace SolarPortal.Application.Interfaces.Services;

/// <summary>
/// Records what an admin did, so screens like PM Surya Ghar can show a decision
/// trail ("kaunse admin ne accept kiya, kisne reject kiya, kab").
///
/// Writes are best-effort by design: a failure to log must never abort the
/// business action that was being logged. The implementation swallows its own
/// errors for that reason.
/// </summary>
public interface IAdminActivityLogger
{
    /// <param name="action">Short verb, e.g. "PMSurya.Accept" or "Fund.Approve".</param>
    /// <param name="entityName">The thing acted on, e.g. "SolarRequest".</param>
    /// <param name="entityId">Its id, as text.</param>
    /// <param name="details">Free text shown in the report.</param>
    Task LogAsync(string userId, string action, string entityName, string entityId,
                  string? details = null, string? ipAddress = null);

    /// <summary>Newest-first log entries for one entity — what the detail pages show.</summary>
    Task<IReadOnlyList<ActivityLog>> GetForEntityAsync(string entityName, string entityId);

    /// <summary>
    /// Newest-first log entries for the Log Report, narrowed by whichever
    /// filters were supplied.
    /// </summary>
    Task<IReadOnlyList<ActivityLog>> SearchAsync(string? userId, string? action,
                                                 DateTime? fromUtc, DateTime? toUtc, int take = 500);
}
