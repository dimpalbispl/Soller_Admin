using SolarPortal.Domain.Common;

namespace SolarPortal.Domain.Entities;

public class Wallet : BaseEntity
{
    public string UserId { get; set; } = string.Empty;
    public decimal TotalIncome { get; set; } = 0;
    public decimal TDS { get; set; } = 0;
    public decimal NetAmount { get; set; } = 0;
    public decimal WithdrawnAmount { get; set; } = 0;
    public decimal PendingBalance { get; set; } = 0;

    // Navigation
    public virtual ApplicationUser? User { get; set; }
    public virtual ICollection<WalletTransaction> Transactions { get; set; } = new List<WalletTransaction>();
    public virtual ICollection<Withdrawal> Withdrawals { get; set; } = new List<Withdrawal>();
}