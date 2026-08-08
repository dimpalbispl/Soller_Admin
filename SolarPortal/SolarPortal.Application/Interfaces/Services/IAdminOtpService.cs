namespace SolarPortal.Application.Interfaces.Services;

/// <summary>
/// Second factor for admin sign-in: a one-time code mailed to the address held
/// on m_usermaster.
///
/// Ported from the legacy SolFit VB page (Default.aspx.vb, CompID 1091), which
/// mailed a six-digit code, stored it, allowed three tries and then forced a
/// fresh request. Same rules here — the differences are that the code lives in
/// AdminLoginOtps instead of the legacy AdminLogin table, and the comparison is
/// done against the stored row rather than a session variable, so it survives a
/// restart and cannot be spoofed by replaying a session.
/// </summary>
public interface IAdminOtpService
{
    /// <summary>
    /// Generates and mails a fresh code for <paramref name="userName"/>, killing
    /// any earlier code still outstanding for that user.
    /// </summary>
    /// <remarks>
    /// <paramref name="fallbackEmail"/> is used when m_usermaster holds no
    /// address for the account. That is the normal case for an Identity-only
    /// login - the seeded "admin" account has no user-master row at all - and
    /// without a fallback those accounts can never be sent a code, so they would
    /// quietly bypass the second factor entirely.
    /// </remarks>
    Task<OtpIssueResult> IssueAsync(string userName, string? ipAddress, string? fallbackEmail = null);

    /// <summary>
    /// Checks a typed code against the newest live challenge for the user.
    /// Each wrong try is counted; the third one burns the challenge.
    /// </summary>
    Task<OtpVerifyResult> VerifyAsync(string userName, string code);

    /// <summary>
    /// Where a code WOULD go, without sending one. Step 3 of the sign-in wizard
    /// shows the masked address before the admin presses Send, and explains up
    /// front when there is no address on file - rather than presenting a button
    /// that silently fails.
    /// </summary>
    Task<OtpIssueResult> PeekTargetAsync(string userName, string? fallbackEmail = null);
}

/// <param name="Success">False when there is no usable e-mail address or the mail could not be sent.</param>
/// <param name="MaskedEmail">"sadhn***@gmail.com" — safe to show on the OTP screen.</param>
/// <param name="Message">Reason to display when <paramref name="Success"/> is false.</param>
/// <param name="ExpiresAt">When the issued code stops working (UTC).</param>
public record OtpIssueResult(bool Success, string? MaskedEmail, string? Message, DateTime? ExpiresAt)
{
    public static OtpIssueResult Fail(string message) => new(false, null, message, null);
}

/// <param name="Success">True only when the code matched a live challenge.</param>
/// <param name="MustRestart">True when the challenge is dead (3 wrong tries, expired, or already used) and a new code is needed.</param>
/// <param name="AttemptsLeft">Tries remaining on the current challenge.</param>
public record OtpVerifyResult(bool Success, bool MustRestart, int AttemptsLeft, string? Message)
{
    public static OtpVerifyResult Ok() => new(true, false, 0, null);
    public static OtpVerifyResult Restart(string message) => new(false, true, 0, message);
    public static OtpVerifyResult Wrong(int attemptsLeft) =>
        new(false, false, attemptsLeft, $"Invalid OTP. {attemptsLeft} attempt(s) left.");
}
