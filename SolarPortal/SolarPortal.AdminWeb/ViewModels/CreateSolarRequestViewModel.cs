using System.ComponentModel.DataAnnotations;
using SolarPortal.Domain.Enums;

namespace SolarPortal.AdminWeb.ViewModels;

public class CreateSolarRequestViewModel
{
    // Step 1 - Personal
    [Required, MaxLength(100)]
    [Display(Name = "Applicant Full Name")]
    public string ApplicantName { get; set; } = string.Empty;

    [Required, Phone]
    [Display(Name = "Mobile Number")]
    public string MobileNumber { get; set; } = string.Empty;

    [Phone]
    [Display(Name = "Alternate Mobile")]
    public string? AlternateMobile { get; set; }

    [Required, EmailAddress]
    public string Email { get; set; } = string.Empty;

    [Required]
    public string Address { get; set; } = string.Empty;

    [Required]
    public string City { get; set; } = string.Empty;

    [Required]
    public string State { get; set; } = string.Empty;

    [Required, RegularExpression(@"^\d{6}$", ErrorMessage = "Enter valid 6-digit pin code")]
    [Display(Name = "Pin Code")]
    public string PinCode { get; set; } = string.Empty;

    [RegularExpression(@"^\d{12}$", ErrorMessage = "Aadhar must be 12 digits")]
    [Display(Name = "Aadhar Number")]
    public string? AadharNumber { get; set; }

    [RegularExpression(@"^[A-Za-z]{5}[0-9]{4}[A-Za-z]{1}$", ErrorMessage = "Enter valid PAN (5 letters + 4 digits + 1 letter, e.g. ABCDE1234F)")]
    [Display(Name = "PAN Number")]
    public string? PANNumber { get; set; }

    // Activation Type
    [Display(Name = "Request Type")]
    public RequestType RequestType { get; set; } = RequestType.WithActivation;

    // Step 2 - Product
    public ConnectionType ConnectionType { get; set; } = ConnectionType.Domestic;

    [Display(Name = "Solar Project")]
    public int? SolarProjectId { get; set; }

    [Range(0.1, 100)]
    [Display(Name = "KV Capacity")]
    public decimal KVCapacity { get; set; } = 1.1m;

    [Display(Name = "Selected Plan")]
    public string? SelectedPlan { get; set; }

    [Range(0, double.MaxValue)]
    [Display(Name = "Plan Amount")]
    public decimal PlanAmount { get; set; }

    // GPS Photo
    public IFormFile? GPSPhoto { get; set; }

    // Light bill / KYC
    [Display(Name = "Light Bill Owner")]
    public string? LightBillOwnerType { get; set; }

    [Display(Name = "Light Bill Relation Name")]
    public string? LightBillRelationName { get; set; }

    [Display(Name = "Light Bill Number")]
    public string? LightBillNumber { get; set; }

    [Display(Name = "Light Bill (file)")]
    public IFormFile? LightBillFile { get; set; }

    [Display(Name = "Relation Proof (file)")]
    public IFormFile? LightBillRelationProof { get; set; }

    // === First Payment (submitted together with the request per spec) ===
    // Payment fields are required for all 3 modes. Server enforces the effective
    // minimum (= min(₹20,000, project amount)) and the upper cap (≤ project total).
    [Display(Name = "Payment Amount")]
    [Range(typeof(decimal), "1", "10000000",
        ErrorMessage = "Enter a valid payment amount.")]
    public decimal PaymentAmount { get; set; }

    [Display(Name = "UTR / Transaction No.")]
    [MaxLength(50)]
    public string? PaymentUTR { get; set; }

    [Display(Name = "Payment Date")]
    [DataType(DataType.Date)]
    public DateTime? PaymentDate { get; set; }

    [Display(Name = "Payment Receipt")]
    public IFormFile? PaymentReceipt { get; set; }

    [Display(Name = "Payment Method")]
    [MaxLength(50)]
    public string? PaymentMethod { get; set; }
}