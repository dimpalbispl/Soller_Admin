using SolarPortal.Domain.Common;

namespace SolarPortal.Domain.Entities;

public class Commission : BaseEntity
{
    public int SolarRequestId { get; set; }
    public string UserId { get; set; } = string.Empty;
    public decimal ProjectAmount { get; set; }
    public decimal CommissionPercentage { get; set; }
    public decimal CommissionAmount { get; set; }
    public bool IsPaid { get; set; } = false;
    public DateTime? PaidAt { get; set; }
    public string? Notes { get; set; }

    public virtual SolarRequest? SolarRequest { get; set; }
}