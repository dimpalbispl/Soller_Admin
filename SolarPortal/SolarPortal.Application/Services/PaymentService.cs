using AutoMapper;
using SolarPortal.Application.DTOs;
using SolarPortal.Application.Interfaces;
using SolarPortal.Application.Interfaces.Services;
using SolarPortal.Domain.Entities;
using SolarPortal.Domain.Enums;

namespace SolarPortal.Application.Services;

public class PaymentService : IPaymentService
{
    /// <summary>
    /// Business rule: cumulative paid amount must be ≥ ₹20,000 before the
    /// workflow can advance past Payment stage. Each payment is allowed
    /// (e.g. a user can pay ₹5,000 + ₹15,000), but the request stays in
    /// Payment stage until the cumulative total clears the minimum.
    /// </summary>
    public const decimal MinimumPaymentThreshold = 20000m;

    private readonly IUnitOfWork _uow;
    private readonly IMapper _mapper;

    public PaymentService(IUnitOfWork uow, IMapper mapper)
    {
        _uow = uow;
        _mapper = mapper;
    }

    public async Task<ServiceResult<PaymentDto>> CreateAsync(CreatePaymentDto dto)
    {
        try
        {
            if (dto.Amount <= 0)
                return ServiceResult<PaymentDto>.Failure("Amount must be greater than zero.");

            // Generate receipt number
            var count = await _uow.Payments.CountAsync() + 1;
            var receiptNumber = $"RCP-{DateTime.Now:yyyy}-{count:D4}";

            var payment = new Payment
            {
                SolarRequestId = dto.SolarRequestId,
                UserId = dto.UserId ?? string.Empty,
                Amount = dto.Amount,
                UTRNumber = dto.UTRNumber ?? string.Empty,
                ReferenceNumber = dto.ReferenceNumber,
                PaymentMethod = dto.PaymentMethod,
                Notes = dto.Notes,
                PaymentDate = dto.PaymentDate,
                ReceiptImagePath = dto.ReceiptImagePath,
                ReceiptNumber = receiptNumber,
                Status = PaymentStatus.Pending,
                IsVerified = false
            };

            await _uow.Payments.AddAsync(payment);
            await _uow.SaveChangesAsync();

            // Compute cumulative paid (verified + just-added pending) for hint message
            var totalPaid = await GetTotalPaidAsync(dto.SolarRequestId);
            var hint = totalPaid >= MinimumPaymentThreshold
                ? $"Cumulative ₹{totalPaid:N0} meets the ₹{MinimumPaymentThreshold:N0} minimum. Awaiting admin verification."
                : $"Cumulative ₹{totalPaid:N0} of ₹{MinimumPaymentThreshold:N0} minimum. Add ₹{MinimumPaymentThreshold - totalPaid:N0} more to unlock the next stage.";

            var result = _mapper.Map<PaymentDto>(payment);
            return ServiceResult<PaymentDto>.Success(result, hint);
        }
        catch (Exception ex)
        {
            return ServiceResult<PaymentDto>.Failure($"Failed to submit payment: {ex.Message}");
        }
    }

    public async Task<IEnumerable<PaymentDto>> GetByRequestIdAsync(int requestId)
    {
        var payments = await _uow.Payments.FindAsync(p => p.SolarRequestId == requestId);
        return _mapper.Map<IEnumerable<PaymentDto>>(payments.OrderByDescending(p => p.CreatedAt));
    }

    public async Task<ServiceResult<bool>> VerifyAsync(int paymentId, string verifiedBy)
    {
        var payment = await _uow.Payments.GetByIdAsync(paymentId);
        if (payment == null)
            return ServiceResult<bool>.Failure("Payment not found");

        payment.IsVerified = true;
        payment.VerifiedBy = verifiedBy;
        payment.VerifiedAt = DateTime.UtcNow;
        payment.Status = PaymentStatus.Completed;
        _uow.Payments.Update(payment);
        await _uow.SaveChangesAsync();

        return ServiceResult<bool>.Success(true, "Payment verified");
    }

    /// <summary>
    /// Cumulative amount of all payments for the request, regardless of status.
    /// Used to enforce the ₹20,000 minimum business rule.
    /// </summary>
    public async Task<decimal> GetTotalPaidAsync(int requestId)
    {
        var payments = await _uow.Payments.FindAsync(p => p.SolarRequestId == requestId);
        return payments.Sum(p => p.Amount);
    }

    /// <summary>
    /// Cumulative amount of payments admin has verified.
    /// </summary>
    public async Task<decimal> GetVerifiedPaidAsync(int requestId)
    {
        var payments = await _uow.Payments.FindAsync(p => p.SolarRequestId == requestId && p.IsVerified);
        return payments.Sum(p => p.Amount);
    }

    public async Task<bool> HasMetMinimumAsync(int requestId)
    {
        var total = await GetTotalPaidAsync(requestId);
        return total >= MinimumPaymentThreshold;
    }
}
