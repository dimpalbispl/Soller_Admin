using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SolarPortal.Application.Interfaces.Services;
using SolarPortal.Domain.Entities;
using SolarPortal.Infrastructure.Data;
using System.Data;
using System.Data.Common;

namespace SolarPortal.Infrastructure.Services;

/// <summary>
/// Bridge authenticates against live DB (m_membermaster / m_usermaster) and
/// returns an ApplicationUser built from raw SQL. We DO NOT call UserManager
/// methods because EF Core's model cache produces stale SQL that fails
/// against the live DB schema.
/// </summary>
public class LiveDbAuthBridge : ILiveDbAuthBridge
{
    private readonly ApplicationDbContext _db;
    private readonly IPasswordHasher<ApplicationUser> _hasher;
    private readonly ILogger<LiveDbAuthBridge> _logger;

    public LiveDbAuthBridge(
        ApplicationDbContext db,
        IPasswordHasher<ApplicationUser> hasher,
        ILogger<LiveDbAuthBridge> logger)
    {
        _db     = db;
        _hasher = hasher;
        _logger = logger;
    }

    public async Task<ApplicationUser?> TryBridgeUserAsync(string idNo, string password)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(idNo) || string.IsNullOrWhiteSpace(password))
                return null;

            var trimmedId = idNo.Trim();

            // 1. Verify against m_membermaster
            var member = await _db.Members
                .AsNoTracking()
                .Where(m => m.IdNo != null && m.Passw != null)
                .FirstOrDefaultAsync(m =>
                    m.IdNo!.Trim() == trimmedId &&
                    m.Passw!.Trim() == password);

            if (member == null)
            {
                _logger.LogInformation("LiveDb bridge: no user match for IdNo={IdNo}", trimmedId);
                return null;
            }

            // 2. Ensure shadow Identity user + role exist (raw SQL only)
            var email = $"member-{trimmedId}@livedb.local";
            await EnsureRoleViaSqlAsync("User");
            await EnsureUserViaSqlAsync(trimmedId, email, password, member.FullName, "User");

            // 3. Load user via raw SQL (bypass UserManager.FindByEmailAsync)
            return await LoadUserBySqlAsync(email);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LiveDb bridge failed for user IdNo={IdNo}", idNo);
            return null;
        }
    }

    public async Task<ApplicationUser?> TryBridgeAdminAsync(string userName, string password)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrWhiteSpace(password))
                return null;

            var trimmedName = userName.Trim();

            var admin = await _db.AdminUsers
                .AsNoTracking()
                .Where(u => u.UserName != null && u.Passw != null)
                .FirstOrDefaultAsync(u =>
                    u.UserName!.Trim() == trimmedName &&
                    u.Passw!.Trim() == password);

            if (admin == null)
            {
                _logger.LogInformation("LiveDb bridge: no admin match for UserName={UserName}", trimmedName);
                return null;
            }

            var email = $"admin-{trimmedName}@livedb.local";
            await EnsureRoleViaSqlAsync("Admin");
            await EnsureUserViaSqlAsync(trimmedName, email, password, trimmedName, "Admin");

            return await LoadUserBySqlAsync(email);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LiveDb bridge failed for admin UserName={UserName}", userName);
            return null;
        }
    }

    // ─── Direct SQL helpers ────────────────────────────────────────────

    private async Task EnsureRoleViaSqlAsync(string roleName)
    {
        var normalized = roleName.ToUpperInvariant();

        var exists = await _db.Database.SqlQueryRaw<int>(
            "SELECT COUNT(1) AS Value FROM Roles WHERE NormalizedName = {0}",
            normalized).FirstOrDefaultAsync();

        if (exists > 0) return;

        var roleId = Guid.NewGuid().ToString();
        await _db.Database.ExecuteSqlRawAsync(
            "INSERT INTO Roles (Id, Name, NormalizedName, ConcurrencyStamp) " +
            "VALUES ({0}, {1}, {2}, {3})",
            roleId, roleName, normalized, Guid.NewGuid().ToString());
    }

    private async Task EnsureUserViaSqlAsync(string desiredUserId, string email, string password, string fullName, string role)
    {
        var normalizedEmail = email.ToUpperInvariant();
        var normalizedUser  = email.ToUpperInvariant();

        var existingId = await _db.Database.SqlQueryRaw<string>(
            "SELECT TOP 1 Id AS Value FROM Users WHERE NormalizedEmail = {0}",
            normalizedEmail).FirstOrDefaultAsync();

        string userId;
        if (existingId == null)
        {
            // Use the live-DB IdNo as the Identity user's Id so that
            // SolarRequests.UserId etc. store the human-readable ID
            // (e.g., "SE123456") instead of a synthetic GUID.
            userId = desiredUserId;
            var displayName = string.IsNullOrWhiteSpace(fullName) ? email : fullName;
            var hashedPw = _hasher.HashPassword(null!, password);

            await _db.Database.ExecuteSqlRawAsync(@"
                INSERT INTO Users
                (Id, UserName, NormalizedUserName, Email, NormalizedEmail, EmailConfirmed,
                 PasswordHash, SecurityStamp, ConcurrencyStamp,
                 PhoneNumberConfirmed, TwoFactorEnabled, LockoutEnabled, AccessFailedCount,
                 FullName, IsActive, CreatedAt)
                VALUES
                ({0}, {1}, {2}, {3}, {4}, 1,
                 {5}, {6}, {7},
                 0, 0, 0, 0,
                 {8}, 1, GETUTCDATE())",
                userId, email, normalizedUser, email, normalizedEmail,
                hashedPw, Guid.NewGuid().ToString(), Guid.NewGuid().ToString(),
                displayName);
        }
        else
        {
            userId = existingId;
        }

        var roleId = await _db.Database.SqlQueryRaw<string>(
            "SELECT TOP 1 Id AS Value FROM Roles WHERE NormalizedName = {0}",
            role.ToUpperInvariant()).FirstOrDefaultAsync();

        if (roleId == null) return;

        var hasRole = await _db.Database.SqlQueryRaw<int>(
            "SELECT COUNT(1) AS Value FROM UserRoles WHERE UserId = {0} AND RoleId = {1}",
            userId, roleId).FirstOrDefaultAsync();

        if (hasRole == 0)
        {
            await _db.Database.ExecuteSqlRawAsync(
                "INSERT INTO UserRoles (UserId, RoleId) VALUES ({0}, {1})",
                userId, roleId);
        }
    }

    /// <summary>
    /// Load ApplicationUser using ADO.NET — completely bypasses EF Core
    /// model cache and UserManager. This is what makes the bridge immune
    /// to "Invalid column name" errors caused by stale EF model snapshots.
    /// </summary>
    private async Task<ApplicationUser?> LoadUserBySqlAsync(string email)
    {
        var normalizedEmail = email.ToUpperInvariant();
        var connection = _db.Database.GetDbConnection();

        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync();

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT TOP 1
                Id, UserName, Email, FullName, IsActive, SecurityStamp,
                ConcurrencyStamp, PhoneNumber, EmailConfirmed
            FROM Users
            WHERE NormalizedEmail = @e";

        var p = cmd.CreateParameter();
        p.ParameterName = "@e";
        p.Value = normalizedEmail;
        cmd.Parameters.Add(p);

        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            return null;

        return new ApplicationUser
        {
            Id              = reader.GetString(0),
            UserName        = reader.IsDBNull(1) ? null : reader.GetString(1),
            Email           = reader.IsDBNull(2) ? null : reader.GetString(2),
            FullName        = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
            IsActive        = !reader.IsDBNull(4) && reader.GetBoolean(4),
            SecurityStamp   = reader.IsDBNull(5) ? null : reader.GetString(5),
            ConcurrencyStamp = reader.IsDBNull(6) ? null : reader.GetString(6),
            PhoneNumber     = reader.IsDBNull(7) ? null : reader.GetString(7),
            EmailConfirmed  = !reader.IsDBNull(8) && reader.GetBoolean(8)
        };
    }
}
