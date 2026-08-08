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

    // ─── Add Fund / Approve Fund (change request point 7) ─────────────────
    // A fund entered by an admin used to be verified the instant it was saved.
    // It now needs a second admin: IsAdminFund puts the row in the Approve Fund
    // queue and it stays unverified until approved there. Payments the user
    // submits themselves never set this flag, so the existing Payment
    // Verification screen behaves exactly as before.
    public bool IsAdminFund { get; set; } = false;
    public string? FundAddedBy { get; set; }
    public string? FundApprovedBy { get; set; }
    public DateTime? FundApprovedAt { get; set; }
    public string? FundRejectionReason { get; set; }

    public virtual SolarRequest? SolarRequest { get; set; }
}