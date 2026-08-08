using System.Linq;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using SolarPortal.Application.Interfaces.Services;
using SolarPortal.Domain.Entities;
using SolarPortal.AdminWeb.ViewModels;

namespace SolarPortal.AdminWeb.Controllers;

[AllowAnonymous]
public class AccountController : Controller
{
    // ─── Pending-OTP session keys ─────────────────────────────────────────
    // Between the password step and the OTP step we remember only WHO passed the
    // password check — never the password itself. Everything needed to complete
    // the sign-in afterwards is re-derived from these.
    private const string SessionOtpUser = "AdminOtp.User";
    private const string SessionOtpKind = "AdminOtp.Kind";      // "bridge" | "identity"
    private const string SessionOtpEmail = "AdminOtp.Email";    // identity path only
    private const string SessionOtpMasked = "AdminOtp.Masked";
    private const string SessionOtpRemember = "AdminOtp.Remember";
    private const string SessionOtpReturn = "AdminOtp.ReturnUrl";
    // How far the visitor legitimately got. Server-side only - a hidden field
    // claiming "password done" would be forgeable, this cannot be.
    private const string SessionOtpStage = "AdminOtp.Stage";
    // Why no code could be sent, when password-only is allowed to proceed.
    private const string SessionOtpProblem = "AdminOtp.Problem";

    private readonly UserManager<ApplicationUser> _userManager;
    private readonly SignInManager<ApplicationUser> _signInManager;
    private readonly ILiveDbAuthBridge _liveDbBridge;
    private readonly IAdminOtpService _otp;
    private readonly IConfiguration _config;
    private readonly ILogger<AccountController> _logger;

    public AccountController(
        UserManager<ApplicationUser> userManager,
        SignInManager<ApplicationUser> signInManager,
        ILiveDbAuthBridge liveDbBridge,
        IAdminOtpService otp,
        IConfiguration config,
        ILogger<AccountController> logger)
    {
        _userManager = userManager;
        _signInManager = signInManager;
        _liveDbBridge = liveDbBridge;
        _otp = otp;
        _config = config;
        _logger = logger;
    }

    /// <summary>Master switch — set "AdminOtp:Enabled" to false to go back to plain password login.</summary>
    private bool OtpEnabled => !bool.TryParse(_config["AdminOtp:Enabled"], out var on) || on;

    /// <summary>
    /// What to do when the admin has no e-mail address on m_usermaster and so
    /// cannot be sent a code. Default TRUE (let them in on the password alone)
    /// because turning OTP on must not lock out every admin whose user-master row
    /// has a blank Email. Set "AdminOtp:AllowLoginWithoutEmail" to false once
    /// every admin has an address on file.
    /// </summary>
    private bool AllowLoginWithoutEmail => !bool.TryParse(_config["AdminOtp:AllowLoginWithoutEmail"], out var allow) || allow;

    // ═══════════════════════════════════════════════════════════════════════════
    // Admin sign-in — two steps:
    //   1. User ID          → the code is mailed
    //   2. Password + OTP   → both checked together, then signed in
    //
    // The stage lives in the SESSION and is checked on entry, so posting straight to
    // step 2 without clearing step 1 sends you back to the start. The auth cookie is
    // issued only when the password AND the code are both accepted.
    //
    // Note the code is mailed on the User ID alone, before the password is checked -
    // that is what lets both fields sit on one screen. It means someone who knows a
    // User ID can cause OTP mails to be sent; the code is useless without the
    // password, and IssueAsync retires the previous code each time, so the worst case
    // is noise in that admin's inbox.
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>How far the visitor has legitimately got. Stored server-side only.</summary>
    private const string StageVerify = "verify";     // user id accepted, code mailed

    private string? PendingStage => HttpContext.Session.GetString(SessionOtpStage);
    private string? PendingUser  => HttpContext.Session.GetString(SessionOtpUser);

    /// <summary>
    /// Guard for step 2. Returns null when the caller may proceed, otherwise the
    /// redirect that sends them back to the start.
    /// </summary>
    private IActionResult? RequireStage(params string[] allowed)
    {
    var stage = PendingStage;
    if (string.IsNullOrEmpty(PendingUser) || string.IsNullOrEmpty(stage) || !allowed.Contains(stage))
    {
        TempData["Warning"] = "Please sign in again.";
        return RedirectToAction(nameof(Login));
    }
    return null;
    }

    // ─── Step 1: User ID ──────────────────────────────────────────────────────
    [HttpGet]
    public IActionResult Login(string? returnUrl = null)
    {
    if (User.Identity?.IsAuthenticated == true)
        return RedirectToAction("Index", "Dashboard", new { area = "SolarPanelAdmin" });

    // Landing here abandons any half-finished sign-in.
    ClearPendingOtp();

    ViewData["ReturnUrl"] = returnUrl;
    return View(new AdminUserIdViewModel { ReturnUrl = returnUrl });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Login(AdminUserIdViewModel model)
    {
    if (!ModelState.IsValid) return View(model);

    var userName = model.UserName.Trim();

    var known = await _liveDbBridge.AdminExistsAsync(userName)
             || await _userManager.FindByEmailAsync(userName) != null
             || await _userManager.FindByNameAsync(userName) != null;

    if (!known)
    {
        ModelState.AddModelError(nameof(model.UserName), "No admin account found for this User ID.");
        return View(model);
    }

    ClearPendingOtp();
    HttpContext.Session.SetString(SessionOtpUser, userName);
    HttpContext.Session.SetString(SessionOtpReturn, model.ReturnUrl ?? string.Empty);
    HttpContext.Session.SetString(SessionOtpStage, StageVerify);

    if (!OtpEnabled)
        return RedirectToAction(nameof(Verify));

    var issued = await _otp.IssueAsync(userName, HttpContext.Connection.RemoteIpAddress?.ToString());

    if (issued.Success)
    {
        HttpContext.Session.SetString(SessionOtpMasked, issued.MaskedEmail ?? string.Empty);
        TempData["Success"] = $"OTP sent to {issued.MaskedEmail}.";
    }
    else if (AllowLoginWithoutEmail)
    {
        // No inbox to mail. Password alone will be accepted on the next screen,
        // but never silently - the reason is shown there.
        HttpContext.Session.SetString(SessionOtpMasked, string.Empty);
        HttpContext.Session.SetString(SessionOtpProblem, issued.Message ?? "the code could not be sent.");
    }
    else
    {
        ClearPendingOtp();
        ModelState.AddModelError(string.Empty, issued.Message ?? "Could not send the OTP.");
        return View(model);
    }

    return RedirectToAction(nameof(Verify));
    }

    // ─── Step 2: Password + OTP, together ─────────────────────────────────────
    [HttpGet]
    public IActionResult Verify()
    {
    var blocked = RequireStage(StageVerify);
    if (blocked != null) return blocked;

    return View(new AdminVerifyViewModel
    {
        UserName = PendingUser,
        MaskedEmail = HttpContext.Session.GetString(SessionOtpMasked),
        OtpProblem = HttpContext.Session.GetString(SessionOtpProblem),
        ReturnUrl = HttpContext.Session.GetString(SessionOtpReturn)
    });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Verify(AdminVerifyViewModel model)
    {
    var blocked = RequireStage(StageVerify);
    if (blocked != null) return blocked;

    // The session is the authority on WHO is signing in - never the posted field.
    var userName = PendingUser!;
    model.UserName = userName;
    model.MaskedEmail = HttpContext.Session.GetString(SessionOtpMasked);

    var otpProblem = HttpContext.Session.GetString(SessionOtpProblem);
    model.OtpProblem = otpProblem;

    // An OTP is required unless there was no way to send one.
    var otpRequired = OtpEnabled && string.IsNullOrEmpty(otpProblem);

    if (string.IsNullOrWhiteSpace(model.Password))
        ModelState.AddModelError(nameof(model.Password), "Please enter your password.");
    if (otpRequired && string.IsNullOrWhiteSpace(model.Otp))
        ModelState.AddModelError(nameof(model.Otp), "Please enter the OTP.");
    if (!ModelState.IsValid) return View(model);

    // ── Password ──
    var bridged = await _liveDbBridge.TryBridgeAdminAsync(userName, model.Password);
    ApplicationUser? user = bridged;
    var kind = "bridge";

    if (user == null)
    {
        var identityUser = await _userManager.FindByEmailAsync(userName)
                        ?? await _userManager.FindByNameAsync(userName);

        if (identityUser is { IsActive: true })
        {
            // Validates WITHOUT issuing the auth cookie - the cookie waits until
            // the OTP has also been accepted.
            var result = await _signInManager.CheckPasswordSignInAsync(identityUser, model.Password, lockoutOnFailure: false);
            if (result.Succeeded)
            {
                var roles = await _userManager.GetRolesAsync(identityUser);
                if (!(roles.Contains("Admin") || roles.Contains("SuperAdmin")))
                {
                    ModelState.AddModelError(string.Empty, "This account is not authorised for the Admin site.");
                    return View(model);
                }
                user = identityUser;
                kind = "identity";
            }
        }
    }

    if (user == null)
    {
        _logger.LogWarning("Failed admin password for {UserName}.", userName);
        ModelState.AddModelError(nameof(model.Password), "Incorrect password.");
        return View(model);
    }

    // ── OTP ──
    // Checked only after the password passes, so a wrong password cannot burn an
    // attempt off the code.
    if (otpRequired)
    {
        var check = await _otp.VerifyAsync(userName, model.Otp);

        if (check.MustRestart)
        {
            ClearPendingOtp();
            TempData["Warning"] = check.Message;
            return RedirectToAction(nameof(Login));
        }

        if (!check.Success)
        {
            ModelState.AddModelError(nameof(model.Otp), check.Message ?? "Invalid OTP.");
            return View(model);
        }
    }
    else
    {
        _logger.LogWarning("Admin {UserName} signed in WITHOUT OTP: {Reason}", userName, otpProblem);
        TempData["Warning"] =
            "Signed in WITHOUT an OTP - " + otpProblem +
            " Set AdminOtp:AllowLoginWithoutEmail to false once every admin has an e-mail on file.";
    }

    var remember = model.RememberMe;
    var returnUrl = HttpContext.Session.GetString(SessionOtpReturn);
    ClearPendingOtp();

    _logger.LogInformation("Admin {UserName} signed in ({Kind}).", userName, kind);
    return await CompleteSignInAsync(user, remember, string.IsNullOrWhiteSpace(returnUrl) ? null : returnUrl);
    }

    // Ask for a fresh code - the old one is retired by IssueAsync.
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ResendOtp()
    {
    var blocked = RequireStage(StageVerify);
    if (blocked != null) return blocked;

    var userName = PendingUser!;
    var issued = await _otp.IssueAsync(userName, HttpContext.Connection.RemoteIpAddress?.ToString());

    if (issued.Success)
    {
        HttpContext.Session.SetString(SessionOtpMasked, issued.MaskedEmail ?? string.Empty);
        HttpContext.Session.Remove(SessionOtpProblem);
        TempData["Success"] = $"A new OTP has been sent to {issued.MaskedEmail}.";
    }
    else
    {
        TempData["Warning"] = issued.Message ?? "Could not resend the OTP.";
    }

    return RedirectToAction(nameof(Verify));
    }
    private async Task<IActionResult> CompleteSignInAsync(ApplicationUser user, bool remember, string? returnUrl)
    {
        await _signInManager.SignInAsync(user, isPersistent: remember);

        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
            return Redirect(returnUrl);

        return RedirectToAction("Index", "Dashboard", new { area = "SolarPanelAdmin" });
    }

    private void ClearPendingOtp()
    {
        foreach (var key in new[]
                 {
                     SessionOtpUser, SessionOtpKind, SessionOtpEmail,
                     SessionOtpMasked, SessionOtpRemember, SessionOtpReturn, SessionOtpStage, SessionOtpProblem
                 })
        {
            HttpContext.Session.Remove(key);
        }
    }

    [HttpGet]
    public IActionResult Register()
    {
        if (User.Identity?.IsAuthenticated == true)
            return RedirectToAction("Index", "Dashboard");
        return View();
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Register(RegisterViewModel model)
    {
        // Normalize PAN to uppercase (regex accepts both cases for user convenience)
        if (!string.IsNullOrWhiteSpace(model.PANNumber))
            model.PANNumber = model.PANNumber.Trim().ToUpperInvariant();

        if (!ModelState.IsValid)
            return View(model);

        var user = new ApplicationUser
        {
            UserName = model.Email,
            Email = model.Email,
            FullName = model.FullName,
            FatherName = model.FatherName,
            MobileNumber = model.MobileNumber,
            Address = model.Address,
            City = model.City,
            State = model.State,
            PinCode = model.PinCode,
            AadharNumber = model.AadharNumber,
            PANNumber = model.PANNumber,
            EmailConfirmed = false, // Require admin approval
            IsActive = false        // Require admin approval per spec
        };

        var result = await _userManager.CreateAsync(user, model.Password);
        if (result.Succeeded)
        {
            await _userManager.AddToRoleAsync(user, "User");
            // Doc uploads at registration can be wired into FileUploadService + DocumentService
            TempData["Success"] = "Registration successful. Please wait for admin approval.";
            return RedirectToAction("Login");
        }

        foreach (var error in result.Errors)
            ModelState.AddModelError(string.Empty, error.Description);

        return View(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize]
    public async Task<IActionResult> Logout()
    {
        await _signInManager.SignOutAsync();
        return RedirectToAction("Login");
    }

    [HttpGet]
    public IActionResult ForgotPassword() => View();

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ForgotPassword(ForgotPasswordViewModel model)
    {
        if (!ModelState.IsValid) return View(model);

        var user = await _userManager.FindByEmailAsync(model.Email);
        if (user == null)
        {
            TempData["Success"] = "If the email exists, a reset link has been sent.";
            return RedirectToAction("Login");
        }

        var token = await _userManager.GeneratePasswordResetTokenAsync(user);
        // In production, send email with reset link
        TempData["Info"] = $"Reset token (dev only): {token}";
        return View("ForgotPasswordConfirmation");
    }

    [HttpGet]
    public IActionResult AccessDenied() => View();
}