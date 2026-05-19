using SolarPortal.Domain.Common;
using SolarPortal.Domain.Enums;

namespace SolarPortal.Domain.Entities;

public class Payment : BaseEntity
{
    public int SolarRequestId { get; set; }
    public string UserId { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string UTRNumber { get; set; } = string.Empty;
    public string? ReferenceNumber { get; set; }
    public string? PaymentMethod { get; set; }
    public DateTime PaymentDate { get; set; }
    public string? ReceiptImagePath { get; set; }
    public string? ReceiptNumber { get; set; } // SCR-2024-001
    public PaymentStatus Status { get; set; } = PaymentStatus.Pending;
    public bool IsVerified { get; set; } = false;
    public string? VerifiedBy { get; set; }
    public DateTime? VerifiedAt { get; set; }
    public string? Notes { get; set; }

    public virtual SolarRequest? SolarRequest { get; set; }
}