namespace SolarPortal.Domain.Entities;

// INC worker withdrawal request. Maps to the existing IncWithdrawals table (no BaseEntity columns).
public class IncWithdrawal
{
    public int Id { get; set; }
    public int WorkerId { get; set; }
    public string? RequestNumber { get; set; }
    public decimal Amount { get; set; }
    public string? BankDetails { get; set; }
    public string Status { get; set; } = "Pending";   // Pending / Approved / Rejected
    public DateTime RequestedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ProcessedAt { get; set; }
    public string? ProcessedBy { get; set; }
    public string? AdminNotes { get; set; }
    public string? RejectionReason { get; set; }
}