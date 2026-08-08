using SolarPortal.Application.DTOs;
using SolarPortal.Application.Interfaces;
using SolarPortal.Application.Interfaces.Services;
using SolarPortal.Domain.Entities;
using SolarPortal.Domain.Enums;

namespace SolarPortal.Application.Services;

/// <inheritdoc />
public class AdminFundService : IAdminFundService
{
    private readonly IUnitOfWork _uow;
    private readonly IPaymentService _payments;
    private readonly ISolarRequestService _requests;
    private readonly INotificationService _notifications;

    public AdminFundService(IUnitOfWork uow, IPaymentService payments,
        ISolarRequestService requests, INotificationService notifications)
    {
        _uow = uow;
        _payments = payments;
        _requests = requests;
        _notifications = notifications;
    }

    public async Task<ServiceResult<Payment>> AddAsync(AddFundInput input)
    {
        try
        {
            if (input.Amount <= 0)
                return ServiceResult<Payment>.Failure("Amount must be greater than zero.");
            if (string.IsNullOrWhiteSpace(input.UtrNumber))
                return ServiceResult<Payment>.Failure("UTR number is required.");

            var req = await _uow.SolarRequests.GetByIdAsync(input.SolarRequestId);
            if (req == null)
                return ServiceResult<Payment>.Failure("Solar request not found.");

            // The same UTR must not already be sitting in the queue for this
            // request — a double-click on Add Fund would otherwise queue the same
            // money twice and both entries would be approvable.
            var utr = input.UtrNumber.Trim();
            var duplicate = (await _uow.Payments.FindAsync(p =>
                                p.SolarRequestId == input.SolarRequestId &&
                                p.UTRNumber == utr))
                            .Any(p => p.Status != PaymentStatus.Rejected);
            if (duplicate)
                return ServiceResult<Payment>.Failure($"A payment with UTR {utr} already exists on this request.");

            var count = await _uow.Payments.CountAsync() + 1;

            var payment = new Payment
            {
                SolarRequestId = input.SolarRequestId,
                UserId = req.UserId,                     // attribute to project owner
                Amount = input.Amount,
                UTRNumber = utr,
                ReferenceNumber = input.ReferenceNumber?.Trim(),
                PaymentDate = input.PaymentDate ?? DateTime.UtcNow,
                PaymentMethod = "Admin Entry",
                ReceiptImagePath = input.ReceiptPath,
                ReceiptNumber = $"RCP-{DateTime.Now:yyyy}-{count:D4}",

                // Point 7: NOT verified on entry any more. It waits in the
                // Approve Fund queue until a second admin confirms it.
                Status = PaymentStatus.Pending,
                IsVerified = false,
                IsAdminFund = true,
                FundAddedBy = input.AdminId,
                Notes = $"[ADMIN FUND by {input.AdminName}] {(input.Notes ?? "")}".Trim()
            };

            await _uow.Payments.AddAsync(payment);
            await _uow.SaveChangesAsync();

            return ServiceResult<Payment>.Success(payment,
                $"Fund of ₹{input.Amount:N0} added and sent for approval. It will not count toward the project total until approved.");
        }
        catch (Exception ex)
        {
            return ServiceResult<Payment>.Failure($"Add fund failed: {ex.InnerException?.Message ?? ex.Message}");
        }
    }

    public async Task<ServiceResult<bool>> ApproveAsync(int paymentId, string approverId, string? note)
    {
        try
        {
            var payment = await _uow.Payments.GetByIdAsync(paymentId);
            if (payment == null) return ServiceResult<bool>.Failure("Fund entry not found.");
            if (!payment.IsAdminFund) return ServiceResult<bool>.Failure("This is not an admin-added fund.");
            if (payment.IsVerified) return ServiceResult<bool>.Failure("This fund is already approved.");
            if (payment.Status == PaymentStatus.Rejected)
                return ServiceResult<bool>.Failure("This fund was rejected. Add a fresh entry instead.");

            // Maker-checker: the admin who added the money must not be the one who
            // confirms it. That separation is the entire reason for splitting the
            // menu, so it is enforced here and not just hidden in the UI.
            if (string.Equals(payment.FundAddedBy, approverId, StringComparison.OrdinalIgnoreCase))
                return ServiceResult<bool>.Failure(
                    "You added this fund, so you cannot approve it. Another admin has to approve it.");

            payment.IsVerified = true;
            payment.Status = PaymentStatus.Completed;
            payment.VerifiedBy = approverId;
            payment.VerifiedAt = DateTime.UtcNow;
            payment.FundApprovedBy = approverId;
            payment.FundApprovedAt = DateTime.UtcNow;
            if (!string.IsNullOrWhiteSpace(note))
                payment.Notes = $"{payment.Notes}\n[APPROVED] {note}".Trim();
            _uow.Payments.Update(payment);
            await _uow.SaveChangesAsync();

            await _notifications.CreateAsync(new CreateNotificationDto
            {
                UserId = payment.UserId,
                SolarRequestId = payment.SolarRequestId,
                Title = "Payment recorded by admin",
                Message = $"A payment of ₹{payment.Amount:N0} (UTR {payment.UTRNumber}) has been recorded and approved on your project.",
                NotificationType = "Payment"
            });

            // Same stage gate the normal verification path uses: an unapproved
            // request still needs its full project amount before PM Surya opens.
            // (An already-approved request is opened by admin approval instead —
            // see SolarRequestService.ApproveAsync.)
            var req = await _uow.SolarRequests.GetByIdAsync(payment.SolarRequestId);
            var verifiedTotal = await _payments.GetVerifiedPaidAsync(payment.SolarRequestId);
            var advanced = false;

            if (req != null &&
                req.PlanAmount > 0 &&
                verifiedTotal >= req.PlanAmount &&
                (req.CurrentStage == ProjectStatus.Registration ||
                 req.CurrentStage == ProjectStatus.ProductSelection ||
                 req.CurrentStage == ProjectStatus.Payment))
            {
                var stage = await _requests.UpdateStageAsync(new UpdateSolarRequestStatusDto
                {
                    Id = payment.SolarRequestId,
                    NewStage = ProjectStatus.PMSurvey,
                    Notes = $"Approved admin fund brought verified total to ₹{verifiedTotal:N0} ≥ project total ₹{req.PlanAmount:N0}."
                }, approverId);
                advanced = stage.IsSuccess;
            }

            return ServiceResult<bool>.Success(true, advanced
                ? $"Fund of ₹{payment.Amount:N0} approved. Verified total ₹{verifiedTotal:N0} — project advanced to PM Surya Ghar."
                : $"Fund of ₹{payment.Amount:N0} approved. Verified total now ₹{verifiedTotal:N0}.");
        }
        catch (Exception ex)
        {
            return ServiceResult<bool>.Failure($"Approve fund failed: {ex.InnerException?.Message ?? ex.Message}");
        }
    }

    public async Task<ServiceResult<bool>> RejectAsync(int paymentId, string approverId, string reason)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(reason))
                return ServiceResult<bool>.Failure("A rejection reason is required.");

            var payment = await _uow.Payments.GetByIdAsync(paymentId);
            if (payment == null) return ServiceResult<bool>.Failure("Fund entry not found.");
            if (!payment.IsAdminFund) return ServiceResult<bool>.Failure("This is not an admin-added fund.");
            if (payment.IsVerified) return ServiceResult<bool>.Failure("This fund is already approved and cannot be rejected.");
            if (payment.Status == PaymentStatus.Rejected) return ServiceResult<bool>.Failure("This fund is already rejected.");

            payment.Status = PaymentStatus.Rejected;
            payment.IsVerified = false;
            payment.FundRejectionReason = reason;
            payment.FundApprovedBy = approverId;      // decision maker, whichever way it went
            payment.FundApprovedAt = DateTime.UtcNow;
            payment.Notes = $"{payment.Notes}\n[REJECTED] {reason}".Trim();
            _uow.Payments.Update(payment);
            await _uow.SaveChangesAsync();

            return ServiceResult<bool>.Success(true, "Fund entry rejected. It does not count toward the project total.");
        }
        catch (Exception ex)
        {
            return ServiceResult<bool>.Failure($"Reject fund failed: {ex.InnerException?.Message ?? ex.Message}");
        }
    }

    public async Task<IReadOnlyList<Payment>> GetPendingAsync() =>
        (await _uow.Payments.FindAsync(p => p.IsAdminFund && !p.IsVerified && p.Status != PaymentStatus.Rejected))
        .OrderByDescending(p => p.CreatedAt)
        .ToList();

    public async Task<IReadOnlyList<Payment>> GetDecidedAsync() =>
        (await _uow.Payments.FindAsync(p => p.IsAdminFund && (p.IsVerified || p.Status == PaymentStatus.Rejected)))
        .OrderByDescending(p => p.CreatedAt)
        .ToList();
}
