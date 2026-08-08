using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http;

namespace SolarPortal.AdminWeb.ViewModels;

public class LoginViewModel
{
    [Required]
    public string Email { get; set; } = string.Empty;

    [Required]
    [DataType(DataType.Password)]
    public string Password { get; set; } = string.Empty;

    [Display(Name = "Remember me")]
    public bool RememberMe { get; set; }

    public string? ReturnUrl { get; set; }
}

/// <summary>
/// Second step of admin sign-in. The username is carried on the form only so the
/// screen can show it — the authoritative copy lives in the session, which is
/// what the controller actually trusts. Nothing here can grant access on its own.
/// </summary>
public class AdminOtpViewModel
{
    [Required(ErrorMessage = "Please enter the OTP.")]
    [Display(Name = "OTP")]
    public string Otp { get; set; } = string.Empty;

    /// <summary>"sadhn***@gmail.com" — shown so the admin knows which inbox to open.</summary>
    public string? MaskedEmail { get; set; }

    public string? ReturnUrl { get; set; }
}

public class RegisterViewModel
{
    [Required, MaxLength(100)]
    [Display(Name = "Full Name")]
    public string FullName { get; set; } = string.Empty;

    [Required, MaxLength(100)]
    [Display(Name = "Father Name")]
    public string FatherName { get; set; } = string.Empty;

    [Required, EmailAddress]
    public string Email { get; set; } = string.Empty;

    [Required, Phone]
    [Display(Name = "Mobile Number")]
    public string MobileNumber { get; set; } = string.Empty;

    [Required, DataType(DataType.Password), MinLength(8)]
    public string Password { get; set; } = string.Empty;

    [DataType(DataType.Password)]
    [Compare("Password", ErrorMessage = "Passwords do not match")]
    [Display(Name = "Confirm Password")]
    public string ConfirmPassword { get; set; } = string.Empty;

    [Required, MaxLength(12)]
    [Display(Name = "Aadhaar Number")]
    public string AadharNumber { get; set; } = string.Empty;

    [Required, MaxLength(10)]
    [RegularExpression(@"^[A-Za-z]{5}[0-9]{4}[A-Za-z]{1}$", ErrorMessage = "Enter valid PAN (5 letters + 4 digits + 1 letter, e.g. ABCDE1234F)")]
    [Display(Name = "PAN Number")]
    public string PANNumber { get; set; } = string.Empty;

    [Required]
    public string Address { get; set; } = string.Empty;

    [Required]
    public string City { get; set; } = string.Empty;

    [Required]
    public string State { get; set; } = string.Empty;

    [Required, MaxLength(6)]
    public string PinCode { get; set; } = string.Empty;

    // Document uploads
    [Display(Name = "Aadhaar Card")]
    public IFormFile? AadharCard { get; set; }

    [Display(Name = "PAN Card")]
    public IFormFile? PANCard { get; set; }

    [Display(Name = "Bank Passbook")]
    public IFormFile? BankPassbook { get; set; }

    [Display(Name = "Light Bill")]
    public IFormFile? LightBill { get; set; }

    [Display(Name = "Property Related Document")]
    public IFormFile? PropertyDocument { get; set; }

    [Display(Name = "GPS Photo")]
    public IFormFile? GPSPhoto { get; set; }
}

public class ForgotPasswordViewModel
{
    [Required, EmailAddress]
    public string Email { get; set; } = string.Empty;
}

/// <summary>
/// Admin sign-in is a four-step wizard, matching the legacy SolFit panel:
///   1. User ID   2. Password   3. Send OTP   4. Verify OTP
///
/// Each step posts only its own field. The authoritative record of how far a
/// visitor has got lives in the SERVER SESSION, never on the form — a hidden
/// field saying "password already checked" would be trivially forged, so the
/// controller re-reads the session stage on every step and refuses anything out
/// of order. Nothing in these models can grant access on its own.
/// </summary>
public class AdminUserIdViewModel
{
    [Required(ErrorMessage = "Please enter your User ID.")]
    [Display(Name = "User ID")]
    public string UserName { get; set; } = string.Empty;

    public string? ReturnUrl { get; set; }
}

/// <summary>Step 2. The username is shown for context; the session holds the real one.</summary>
public class AdminPasswordViewModel
{
    [Required(ErrorMessage = "Please enter your password.")]
    [DataType(DataType.Password)]
    public string Password { get; set; } = string.Empty;

    [Display(Name = "Remember me")]
    public bool RememberMe { get; set; }

    /// <summary>Display only — read back from the session, not trusted from the post.</summary>
    public string? UserName { get; set; }

    public string? ReturnUrl { get; set; }
}

/// <summary>Step 3. Nothing to type — just the confirmation before a code is mailed.</summary>
public class AdminSendOtpViewModel
{
    public string? UserName { get; set; }

    /// <summary>"sadhn***@gmail.com" — so the admin knows which inbox to open.</summary>
    public string? MaskedEmail { get; set; }

    /// <summary>Set when there is no address to mail; the view explains rather than dead-ends.</summary>
    public string? Problem { get; set; }

    public string? ReturnUrl { get; set; }
}


/// <summary>
/// Step 2: password AND the mailed code, together on one screen.
///
/// UserName and MaskedEmail are display-only - the controller reads the real ones
/// from the session, so nothing posted here can change WHO is signing in.
/// </summary>
public class AdminVerifyViewModel
{
    [DataType(DataType.Password)]
    public string Password { get; set; } = string.Empty;

    [Display(Name = "OTP")]
    public string Otp { get; set; } = string.Empty;

    [Display(Name = "Remember me")]
    public bool RememberMe { get; set; }

    public string? UserName { get; set; }

    /// <summary>Masked address, so the admin knows which inbox to open.</summary>
    public string? MaskedEmail { get; set; }

    /// <summary>Set when no code could be sent; the screen then asks for the password only.</summary>
    public string? OtpProblem { get; set; }

    public string? ReturnUrl { get; set; }
}
