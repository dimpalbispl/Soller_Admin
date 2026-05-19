using Microsoft.AspNetCore.Identity;
using SolarPortal.Domain.Common;

namespace SolarPortal.Domain.Entities;

public class ApplicationUser : IdentityUser
{
    public string FullName { get; set; } = string.Empty;
    public string? FatherName { get; set; }
    public string? MobileNumber { get; set; }
    public string? Address { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
    public string? PinCode { get; set; }
    public string? AadharNumber { get; set; }
    public string? PANNumber { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }

    // Navigation
    public virtual ICollection<SolarRequest> SolarRequests { get; set; } = new List<SolarRequest>();
    public virtual ICollection<Notification> Notifications { get; set; } = new List<Notification>();
}