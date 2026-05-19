using SolarPortal.Application.DTOs;

namespace SolarPortal.Application.Interfaces.Services;

public interface IPaymentService
{
    Task<ServiceResult<PaymentDto>> CreateAsync(CreatePaymentDto dto);
    Task<IEnumerable<PaymentDto>> GetByRequestIdAsync(int requestId);
    Task<ServiceResult<bool>> VerifyAsync(int paymentId, string verifiedBy);

    /// <summary>Cumulative payments (any status) for the request.</summary>
    Task<decimal> GetTotalPaidAsync(int requestId);

    /// <summary>Cumulative verified payments for the request.</summary>
    Task<decimal> GetVerifiedPaidAsync(int requestId);

    /// <summary>True if cumulative payments meet the ₹20,000 minimum threshold.</summary>
    Task<bool> HasMetMinimumAsync(int requestId);
}
