using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SolarPortal.Domain.Entities;

namespace SolarPortal.Infrastructure.Data;

/// <summary>
/// Startup bootstrap.
/// - Does NOT call MigrateAsync (would overwrite manually-created AspNet tables
///   in the live DB with partial schemas). Tables are managed by SETUP-IdentityTables.sql.
/// - Wrapped in try-catch so a transient DB hiccup doesn't crash the host.
/// </summary>
public class DbSeeder
{
    private readonly IServiceProvider _services;
    public DbSeeder(IServiceProvider services) => _services = services;

    public async Task SeedAsync()
    {
        using var scope = _services.CreateScope();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<DbSeeder>>();

        try
        {
            // No-op. AspNet tables are created by SETUP-IdentityTables.sql
            // and Solar workflow tables (if needed) should be added manually
            // to the live DB. EF migrations are NOT applied to the live DB.
            await Task.CompletedTask;
            logger.LogInformation("DbSeeder: skipping all DB schema work (live DB managed externally).");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "DbSeeder failed (non-fatal).");
        }
    }
}
