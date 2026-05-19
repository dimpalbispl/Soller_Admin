using SolarPortal.Domain.Enums;

namespace SolarPortal.Application.DTOs;

public class WithdrawalDto
{
    public int Id { get; set; }
    public int WalletId { get; set; }
    public decimal Amount { get; set; }
    public ApprovalStatus Status { get; set; }
    public string? Remarks { get; set; }
    public DateTime RequestedDate { get; set; }
    public DateTime? ProcessedDate { get; set; }
}