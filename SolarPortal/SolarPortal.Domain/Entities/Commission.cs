using SolarPortal.Domain.Common;

namespace SolarPortal.Domain.Entities;

public class Commission : BaseEntity
{
    public int SolarRequestId { get; set; }
    public string UserId { get; set; } = string.Empty;

    // INC worker who earned this commission. Nullable because legacy rows may not have one.
    public int? WorkerId { get; set; }

    public decimal ProjectAmount { get; set; }
    public decimal CommissionPercentage { get; set; }
    public decimal CommissionAmount { get; set; }
    public bool IsPaid { get; set; } = false;
    public DateTime? PaidAt { get; set; }
    public string? PaidBy { get; set; }        // admin who marked it paid
    public string? PaymentReference { get; set; } // UTR / cheque no. when marking paid
    public string? Notes { get; set; }

    public virtual SolarRequest? SolarRequest { get; set; }
    public virtual Worker? Worker { get; set; }
}