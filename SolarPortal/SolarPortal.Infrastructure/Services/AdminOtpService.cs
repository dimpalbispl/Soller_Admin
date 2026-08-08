using System.Data;
using System.Net;
using System.Net.Mail;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SolarPortal.Application.Interfaces.Services;
using SolarPortal.Domain.Entities;
using SolarPortal.Infrastructure.Data;

namespace SolarPortal.Infrastructure.Services;

/// <summary>
/// Issues and verifies the admin login OTP.
///
/// SMTP settings are taken from the legacy M_CompanyMaster row first — the same
/// mailbox the old SolFit VB page sent from — so an admin who already receives
/// OTPs from the legacy panel keeps receiving them from the same address here,
/// with no extra configuration. An "Smtp" section in appsettings.json overrides
/// it when present, which is what local/dev environments use.
/// </summary>
public class AdminOtpService : IAdminOtpService
{
    /// <summary>Matches the legacy page's 5-minute window.</summary>
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    /// <summary>Legacy rule: "You can try 3 times only."</summary>
    private const int MaxAttempts = 3;

    private readonly ApplicationDbContext _db;
    private readonly IConfiguration _config;
    private readonly ILogger<AdminOtpService> _logger;

    public AdminOtpService(ApplicationDbContext db, IConfiguration config, ILogger<AdminOtpService> logger)
    {
        _db = db;
        _config = config;
        _logger = logger;
    }

    public async Task<OtpIssueResult> IssueAsync(string userName, string? ipAddress, string? fallbackEmail = null)
    {
        var name = (userName ?? string.Empty).Trim();
        if (name.Length == 0)
            return OtpIssueResult.Fail("Username is required.");

        var admin = await _db.AdminUsers
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.UserName != null && u.UserName.Trim() == name);

        var email = admin?.Email?.Trim();

        // An Identity-only admin (no m_usermaster row, e.g. the seeded "admin")
        // still has an address on its own account. Use it, otherwise that whole
        // class of account silently skips the second factor.
        if (!IsMailable(email))
            email = fallbackEmail?.Trim();

        if (!IsMailable(email))
        {
            // Without a mailbox there is nowhere to send the code. Say so plainly
            // rather than silently letting the admin in — the whole point of this
            // step is that a password alone is not enough.
            return OtpIssueResult.Fail(
                "No e-mail address is set for this admin account, so an OTP cannot be sent. " +
                "Please ask IT to add one on the user master.");
        }

        var code = GenerateCode();
        var now = DateTime.UtcNow;
        var row = new AdminLoginOtp
        {
            UserName = name,
            EmailId = email,
            MobileNo = admin?.MobileNo?.ToString("0"),
            Otp = code,
            IssuedAt = now,
            ExpiresAt = now.Add(Lifetime),
            IpAddress = ipAddress
        };

        try
        {
            // Retire anything still outstanding so an older code can never be replayed.
            var live = await _db.AdminLoginOtps
                .Where(o => o.UserName == name && !o.IsUsed)
                .ToListAsync();
            foreach (var old in live)
            {
                old.IsUsed = true;
                old.UsedAt = DateTime.UtcNow;
            }

            _db.AdminLoginOtps.Add(row);
            await _db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            // Most likely cause: ADD-AdminPanelChangeRequest.sql has not been run,
            // so AdminLoginOtps does not exist. Report it as "OTP unavailable"
            // rather than throwing out of the login POST — how that is handled
            // (block, or fall back to password-only) is the caller's decision.
            _logger.LogError(ex, "Could not store the admin OTP for {UserName}", name);
            return OtpIssueResult.Fail("The OTP service is unavailable right now. Please try again shortly.");
        }

        // The row is saved BEFORE the mail goes out. If sending throws we still
        // have the audit record of the attempt, and the admin simply asks for a
        // new code — better than a code that was mailed but never recorded.
        try
        {
            await SendAsync(email!, code, name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not mail admin OTP to {Email}", email);

            // Surface the real reason. This screen is admin-only, and a generic
            // "try again in a moment" is unfixable - it hides whether the problem
            // is an unconfigured host, bad credentials or a blocked port, and the
            // login then silently falls through to password-only.
            var reason = ex.InnerException?.Message ?? ex.Message;
            return OtpIssueResult.Fail($"Could not send the OTP e-mail: {reason}");
        }

        return new OtpIssueResult(true, MaskEmail(email!), null, row.ExpiresAt);
    }

    public async Task<OtpIssueResult> PeekTargetAsync(string userName, string? fallbackEmail = null)
    {
        var email = await ResolveTargetEmailAsync(userName, fallbackEmail);
        return email == null
            ? OtpIssueResult.Fail(NoEmailMessage)
            : new OtpIssueResult(true, MaskEmail(email), null, null);
    }

    private const string NoEmailMessage =
        "No e-mail address is set for this admin account, so an OTP cannot be sent. " +
        "Please ask IT to add one on the user master.";

    /// <summary>
    /// The address a code goes to: m_usermaster first, then the account's own
    /// address. Shared by IssueAsync and PeekTargetAsync so the screen can never
    /// promise one inbox and the mail go to another.
    /// </summary>
    private async Task<string?> ResolveTargetEmailAsync(string userName, string? fallbackEmail)
    {
        var name = (userName ?? string.Empty).Trim();
        if (name.Length == 0) return null;

        var admin = await _db.AdminUsers.AsNoTracking()
            .FirstOrDefaultAsync(u => u.UserName != null && u.UserName.Trim() == name);

        var email = admin?.Email?.Trim();
        if (!IsMailable(email))
            email = fallbackEmail?.Trim();

        return IsMailable(email) ? email : null;
    }

    /// <summary>
    /// A real inbox we can actually send to.
    ///
    /// LiveDbAuthBridge invents "admin-NAME@livedb.local" as the Identity e-mail
    /// for a bridged admin. It is a placeholder, not an address - mailing an OTP
    /// there either bounces or vanishes, and the login then falls through to
    /// password-only WITHOUT anyone realising the second factor never happened.
    /// </summary>
    private static bool IsMailable(string? email)
    {
        if (string.IsNullOrWhiteSpace(email)) return false;
        var e = email.Trim();
        if (!e.Contains('@')) return false;
        return !e.EndsWith("@livedb.local", StringComparison.OrdinalIgnoreCase);
    }
    public async Task<OtpVerifyResult> VerifyAsync(string userName, string code)
    {
        var name = (userName ?? string.Empty).Trim();
        var typed = (code ?? string.Empty).Trim();

        if (typed.Length == 0)
            return OtpVerifyResult.Wrong(MaxAttempts);

        var row = await _db.AdminLoginOtps
            .Where(o => o.UserName == name)
            .OrderByDescending(o => o.Id)
            .FirstOrDefaultAsync();

        if (row == null)
            return OtpVerifyResult.Restart("No OTP was requested. Please sign in again.");

        var now = DateTime.UtcNow;

        if (row.IsUsed)
            return OtpVerifyResult.Restart("That OTP has already been used. Please sign in again.");

        if (row.IsExpired(now))
            return OtpVerifyResult.Restart("The OTP has expired. Please sign in again to get a new one.");

        if (row.AttemptCount >= MaxAttempts)
            return OtpVerifyResult.Restart("You have tried 3 times with an invalid OTP. Please sign in again.");

        if (!string.Equals(row.Otp, typed, StringComparison.Ordinal))
        {
            row.AttemptCount++;
            await _db.SaveChangesAsync();

            var left = MaxAttempts - row.AttemptCount;
            return left <= 0
                ? OtpVerifyResult.Restart("You have tried 3 times with an invalid OTP. Please sign in again.")
                : OtpVerifyResult.Wrong(left);
        }

        row.IsUsed = true;
        row.UsedAt = now;
        await _db.SaveChangesAsync();
        return OtpVerifyResult.Ok();
    }

    // ─── helpers ──────────────────────────────────────────────────────────

    /// <summary>Six digits, 100001-999999, matching the legacy Random.Next range but drawn from a crypto RNG.</summary>
    private static string GenerateCode() =>
        RandomNumberGenerator.GetInt32(100001, 1000000).ToString();

    /// <summary>"sadhn***@gmail.com" — first five characters kept, exactly like the legacy MaskEmail.</summary>
    public static string MaskEmail(string email)
    {
        var at = email.IndexOf('@');
        if (at <= 0) return email;

        var local = email[..at];
        var domain = email[at..];
        var visible = Math.Min(5, local.Length);
        return local[..visible] + new string('*', Math.Max(0, local.Length - visible)) + domain;
    }

    private async Task SendAsync(string toAddress, string code, string userName)
    {
        var (host, port, from, password, enableSsl, companyName) = await ResolveSmtpAsync();

        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(from))
        {
            // Name the missing piece and both places it can come from. "Not
            // configured" on its own sends the admin hunting through two systems.
            var hostState = string.IsNullOrWhiteSpace(host) ? "MISSING" : host;
            var fromState = string.IsNullOrWhiteSpace(from) ? "MISSING" : from;
            throw new InvalidOperationException(
                $"SMTP is not configured (host={hostState}, from={fromState}). " +
                "Either fill Smtp:Host and Smtp:From in appsettings.json, or set " +
                "mailHost and CompMail (plus mailPass) on the legacy M_CompanyMaster row.");
        }

        var body = $@"
<table style=""margin:0;padding:10px;font-size:12px;font-family:Verdana,Arial,Helvetica,sans-serif;line-height:23px;width:100%"">
  <tr><td>
    <span style=""color:#990000;font-weight:bold"">{WebUtility.HtmlEncode(companyName)},</span><br /><br />
    You recently tried to sign in to the Solar Admin Panel as
    <b>{WebUtility.HtmlEncode(userName)}</b>.<br />
    Please enter the OTP below to complete the sign-in.<br />
    <span style=""color:#0099FF;font-weight:bold;font-size:16px"">OTP : {code}</span><br /><br />
    This code is valid for 5 minutes and can be tried 3 times.<br />
    If this wasn't you, ignore this mail and your password stays safe.
  </td></tr>
</table>";

        using var message = new MailMessage(new MailAddress(from!), new MailAddress(toAddress))
        {
            Subject = "Confirm Login — Solar Admin Panel",
            Body = body,
            IsBodyHtml = true
        };

        using var smtp = new SmtpClient(host, port)
        {
            EnableSsl = enableSsl,
            DeliveryMethod = SmtpDeliveryMethod.Network,
            UseDefaultCredentials = false
        };
        if (!string.IsNullOrWhiteSpace(password))
            smtp.Credentials = new NetworkCredential(from, password);

        await smtp.SendMailAsync(message);
    }

    /// <summary>
    /// appsettings "Smtp" wins when its Host is filled in; otherwise we fall back
    /// to M_CompanyMaster, the legacy app's mail configuration. Reading that table
    /// is best-effort — it is a legacy table this app does not own, so a missing
    /// table or column must not break the login page with an unhandled exception.
    /// </summary>
    private async Task<(string? host, int port, string? from, string? password, bool ssl, string company)> ResolveSmtpAsync()
    {
        var section = _config.GetSection("Smtp");
        var host = section["Host"];
        var from = section["From"];
        var password = section["Password"];
        var port = int.TryParse(section["Port"], out var p) ? p : 587;
        var ssl = !bool.TryParse(section["EnableSsl"], out var s) || s;
        var company = section["CompanyName"] ?? "Solar Portal";

        if (!string.IsNullOrWhiteSpace(host) && !string.IsNullOrWhiteSpace(from))
            return (host, port, from, password, ssl, company);

        try
        {
            var conn = _db.Database.GetDbConnection();
            if (conn.State != ConnectionState.Open) await conn.OpenAsync();

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT TOP 1 CompName, CompMail, mailPass, mailHost FROM M_CompanyMaster";
            await using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                var legacyCompany = reader.IsDBNull(0) ? company : reader.GetString(0);
                var legacyFrom = reader.IsDBNull(1) ? null : reader.GetString(1)?.Trim();
                var legacyPass = reader.IsDBNull(2) ? null : reader.GetString(2)?.Trim();
                var legacyHost = reader.IsDBNull(3) ? null : reader.GetString(3)?.Trim();

                return (host ?? legacyHost,
                        port,
                        from ?? legacyFrom,
                        password ?? legacyPass,
                        ssl,
                        string.IsNullOrWhiteSpace(legacyCompany) ? company : legacyCompany);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read SMTP settings from M_CompanyMaster; falling back to configuration.");
        }

        return (host, port, from, password, ssl, company);
    }
}
