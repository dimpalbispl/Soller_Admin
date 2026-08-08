using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SolarPortal.Application.Interfaces.Services;
using SolarPortal.Domain.Entities;
using SolarPortal.Infrastructure.Data;

namespace SolarPortal.Infrastructure.Services;

/// <summary>
/// ActivityLog-backed implementation. Uses the DbContext directly rather than
/// the unit of work: the log is written alongside a business action that has its
/// own change-tracking, and sharing a UoW would let a stray SaveChanges from
/// here flush half-finished work.
/// </summary>
public class AdminActivityLogger : IAdminActivityLogger
{
    private readonly ApplicationDbContext _db;
    private readonly ILogger<AdminActivityLogger> _logger;

    public AdminActivityLogger(ApplicationDbContext db, ILogger<AdminActivityLogger> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task LogAsync(string userId, string action, string entityName, string entityId,
                               string? details = null, string? ipAddress = null)
    {
        try
        {
            _db.ActivityLogs.Add(new ActivityLog
            {
                UserId = userId ?? string.Empty,
                Action = Trim(action, 100),
                EntityName = Trim(entityName, 100),
                EntityId = Trim(entityId, 50),
                Details = details,
                IpAddress = Trim(ipAddress ?? "-", 45),
                Timestamp = DateTime.UtcNow
            });
            await _db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            // Losing an audit line is bad; failing the admin's action because the
            // audit line could not be written is worse.
            _logger.LogError(ex, "Could not write activity log {Action} for {Entity}#{Id}",
                             action, entityName, entityId);
        }
    }

    public async Task<IReadOnlyList<ActivityLog>> GetForEntityAsync(string entityName, string entityId)
    {
        try
        {
            return await _db.ActivityLogs
                .AsNoTracking()
                .Where(l => l.EntityName == entityName && l.EntityId == entityId)
                .OrderByDescending(l => l.Timestamp)
                .ToListAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not read activity log for {Entity}#{Id}", entityName, entityId);
            return Array.Empty<ActivityLog>();
        }
    }

    public async Task<IReadOnlyList<ActivityLog>> SearchAsync(string? userId, string? action,
                                                              DateTime? fromUtc, DateTime? toUtc, int take = 500)
    {
        try
        {
            var q = _db.ActivityLogs.AsNoTracking();

            if (!string.IsNullOrWhiteSpace(userId))
                q = q.Where(l => l.UserId == userId);
            if (!string.IsNullOrWhiteSpace(action))
                q = q.Where(l => l.Action.StartsWith(action));
            if (fromUtc.HasValue)
                q = q.Where(l => l.Timestamp >= fromUtc.Value);
            if (toUtc.HasValue)
                q = q.Where(l => l.Timestamp <= toUtc.Value);

            return await q.OrderByDescending(l => l.Timestamp).Take(take).ToListAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not search the activity log");
            return Array.Empty<ActivityLog>();
        }
    }

    private static string Trim(string? value, int max)
    {
        var v = value ?? string.Empty;
        return v.Length <= max ? v : v[..max];
    }
}
