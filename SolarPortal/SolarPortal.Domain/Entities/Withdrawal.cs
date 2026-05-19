using SolarPortal.Domain.Common;
using SolarPortal.Domain.Enums;

namespace SolarPortal.Domain.Entities;

public class Withdrawal : BaseEntity
{
    public int WalletId { get; set; }
    public decimal Amount { get; set; }
    public ApprovalStatus Status { get; set; } = ApprovalStatus.Pending;
    public string? Remarks { get; set; }
    public DateTime RequestedDate { get; set; } = DateTime.UtcNow;
    public DateTime? ProcessedDate { get; set; }

    // Navigation
    public virtual Wallet? Wallet { get; set; }
}