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
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly SignInManager<ApplicationUser> _signInManager;
    private readonly ILiveDbAuthBridge _liveDbBridge;
    private readonly ILogger<AccountController> _logger;

    public AccountController(
        UserManager<ApplicationUser> userManager,
        SignInManager<ApplicationUser> signInManager,
        ILiveDbAuthBridge liveDbBridge,
        ILogger<AccountController> logger)
    {
        _userManager = userManager;
        _signInManager = signInManager;
        _liveDbBridge = liveDbBridge;
        _logger = logger;
    }

    [HttpGet]
    public IActionResult Login(string? returnUrl = null)
    {
        if (User.Identity?.IsAuthenticated == true)
            return RedirectToAction("Index", "Dashboard");

        ViewData["ReturnUrl"] = returnUrl;
        return View();
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Login(LoginViewModel model, string? returnUrl = null)
    {
        if (!ModelState.IsValid)
            return View(model);

        // ─── LiveDB bridge ────────────────────────────────────────────────
        // Bridge verifies against m_usermaster and returns the loaded
        // ApplicationUser (via raw ADO.NET) or null.
        var bridgedUser = await _liveDbBridge.TryBridgeAdminAsync(model.Email, model.Password);

        ApplicationUser? user;

        if (bridgedUser != null)
        {
            user = bridgedUser;
            await _signInManager.SignInAsync(user, isPersistent: model.RememberMe);
            _logger.LogInformation("Admin {Email} logged in via live DB bridge.", model.Email);
            return RedirectToAction("Index", "Dashboard", new { area = "SolarPanelAdmin" });
        }

        // ─── Fallback to standard Identity flow for legacy demo accounts ──
        user = await _userManager.FindByEmailAsync(model.Email);
        if (user == null || !user.IsActive)
        {
            ModelState.AddModelError(string.Empty, "Invalid login attempt.");
            return View(model);
        }

        var result = await _signInManager.PasswordSignInAsync(
            model.Email, model.Password, model.RememberMe, lockoutOnFailure: false);

        if (result.Succeeded)
        {
            _logger.LogInformation("User {Email} logged in.", model.Email);
            var roles = await _userManager.GetRolesAsync(user);

            // ── ADMIN SITE — only Admin / SuperAdmin allowed ───────────────
            // Reject any other role even when credentials are valid.
            if (!(roles.Contains("Admin") || roles.Contains("SuperAdmin")))
            {
                await _signInManager.SignOutAsync();
                ModelState.AddModelError(string.Empty,
                    "This account is not authorised for the Admin site.");
                return View(model);
            }

            return RedirectToAction("Index", "Dashboard", new { area = "SolarPanelAdmin" });
        }

        if (result.IsLockedOut)
            ModelState.AddModelError(string.Empty, "Account locked. Try after 5 minutes.");
        else
            ModelState.AddModelError(string.Empty, "Invalid email or password.");

        return View(model);
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
