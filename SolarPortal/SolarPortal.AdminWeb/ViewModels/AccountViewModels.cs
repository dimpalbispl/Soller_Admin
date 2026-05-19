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