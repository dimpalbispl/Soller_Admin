using SolarPortal.Domain.Common;
using SolarPortal.Domain.Enums;

namespace SolarPortal.Domain.Entities;

public class SolarAccount : BaseEntity
{
    public string AccountNumber { get; set; } = string.Empty; // SA-001
    public string UserId { get; set; } = string.Empty;
    public int SolarRequestId { get; set; }
    public decimal ProjectAmount { get; set; }
    public decimal DepositAmount { get; set; }
    public decimal DueAmount { get; set; }
    public ProjectStatus CurrentStatus { get; set; } = ProjectStatus.Registration;
    public bool IsFrozen { get; set; } = false;

    // Navigation
    public virtual ApplicationUser? User { get; set; }
    public virtual SolarRequest? SolarRequest { get; set; }
    public virtual ICollection<WalletTransaction> WalletTransactions { get; set; } = new List<WalletTransaction>();
}