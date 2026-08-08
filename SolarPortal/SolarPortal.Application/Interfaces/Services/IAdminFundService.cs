using SolarPortal.Application.DTOs;
using SolarPortal.Domain.Entities;

namespace SolarPortal.Application.Interfaces.Services;

/// <summary>
/// Admin-entered funds, split into the two steps the change request asks for
/// (point 7: "Admin me Add Fund → 2 step. Alag 2 menu: Add Fund, Approve Fund").
///
/// Adding no longer credits anything: it writes an UNVERIFIED payment flagged
/// IsAdminFund, which shows up in the Approve Fund queue. Only approval marks it
/// verified and lets it count toward the project total. That makes it a
/// maker-checker pair, so one admin cannot both create and confirm money.
///
/// The logic lives here rather than in a controller because two entry points use
/// it — the Add Fund menu and the older "Add payment" modal on Payment
/// Verification — and they must behave identically.
/// </summary>
public interface IAdminFundService
{
    Task<ServiceResult<Payment>> AddAsync(AddFundInput input);

    /// <summary>
    /// Confirms the fund: marks it verified and, if that completes the project
    /// total, advances the request the same way payment verification does.
    /// </summary>
    Task<ServiceResult<bool>> ApproveAsync(int paymentId, string approverId, string? note);

    Task<ServiceResult<bool>> RejectAsync(int paymentId, string approverId, string reason);

    /// <summary>Admin funds awaiting approval, newest first.</summary>
    Task<IReadOnlyList<Payment>> GetPendingAsync();

    /// <summary>Admin funds already approved or rejected, newest first.</summary>
    Task<IReadOnlyList<Payment>> GetDecidedAsync();
}

public class AddFundInput
{
    public int SolarRequestId { get; set; }
    public decimal Amount { get; set; }
    public string UtrNumber { get; set; } = string.Empty;
    public DateTime? PaymentDate { get; set; }
    public string? ReferenceNumber { get; set; }
    public string? Notes { get; set; }

    /// <summary>Already-uploaded receipt path — the controller owns the file upload.</summary>
    public string? ReceiptPath { get; set; }

    public string AdminId { get; set; } = string.Empty;
    public string AdminName { get; set; } = "Admin";
}
