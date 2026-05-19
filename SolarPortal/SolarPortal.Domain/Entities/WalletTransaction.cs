using SolarPortal.Domain.Common;

namespace SolarPortal.Domain.Entities;

public class WalletTransaction : BaseEntity
{
    public int WalletId { get; set; }
    public int? SolarAccountId { get; set; }
    public string TransactionType { get; set; } = string.Empty; // Income, Withdrawal, etc.
    public decimal Amount { get; set; }
    public string Description { get; set; } = string.Empty;
    public DateTime TransactionDate { get; set; } = DateTime.UtcNow;

    // Navigation
    public virtual Wallet? Wallet { get; set; }
    public virtual SolarAccount? SolarAccount { get; set; }
}