using SolarPortal.Domain.Enums;

namespace SolarPortal.Application.DTOs;

public class PaymentDto
{
    public int Id { get; set; }
    public int SolarRequestId { get; set; }
    public string RequestNumber { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string UTRNumber { get; set; } = string.Empty;
    public string? ReferenceNumber { get; set; }
    public string? PaymentMethod { get; set; }
    public DateTime PaymentDate { get; set; }
    public string? ReceiptImagePath { get; set; }
    public string? ReceiptNumber { get; set; }
    public string? Notes { get; set; }
    public PaymentStatus Status { get; set; }
    public bool IsVerified { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class CreatePaymentDto
{
    public int SolarRequestId { get; set; }
    public string? UserId { get; set; }
    public decimal Amount { get; set; }
    public string? UTRNumber { get; set; }
    public string? ReferenceNumber { get; set; }
    public string? PaymentMethod { get; set; }
    public string? Notes { get; set; }
    public DateTime PaymentDate { get; set; }
    public string? ReceiptImagePath { get; set; }
}
