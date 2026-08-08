namespace SolarPortal.Domain.Entities;

/// <summary>
/// One issued admin-login OTP.
///
/// Mirrors the legacy SolFit VB flow (Default.aspx.vb, CompID 1091): after the
/// username + password check passes, a six-digit code is mailed to the address
/// on m_usermaster and the admin has to type it back. The row is the record of
/// that challenge — it is what makes the code single-use, time-limited and
/// capped at three wrong attempts.
///
/// Deliberately NOT the legacy AdminLogin table: that one belongs to the old
/// ASP.NET app, and writing into it from here would couple the two deployments.
/// </summary>
public class AdminLoginOtp
{
    public int Id { get; set; }

    /// <summary>m_usermaster.UserName the code was issued for.</summary>
    public string UserName { get; set; } = string.Empty;

    public string? EmailId { get; set; }
    public string? MobileNo { get; set; }

    public string Otp { get; set; } = string.Empty;

    public DateTime IssuedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; }

    /// <summary>Wrong-code tries so far. At 3 the challenge is dead and a new OTP must be requested.</summary>
    public int AttemptCount { get; set; }

    public bool IsUsed { get; set; }
    public DateTime? UsedAt { get; set; }

    public string? IpAddress { get; set; }

    public bool IsExpired(DateTime nowUtc) => nowUtc > ExpiresAt;

    /// <summary>Still worth checking a typed code against.</summary>
    public bool IsLive(DateTime nowUtc) => !IsUsed && AttemptCount < 3 && !IsExpired(nowUtc);
}
