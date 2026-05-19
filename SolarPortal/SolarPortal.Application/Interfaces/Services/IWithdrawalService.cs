using SolarPortal.Application.DTOs;
using SolarPortal.Domain.Enums;

namespace SolarPortal.Application.Interfaces.Services;

public interface IWithdrawalService
{
    Task<WithdrawalDto?> GetByIdAsync(int id);
    Task<IEnumerable<WithdrawalDto>> GetByWalletIdAsync(int walletId);
    Task<WithdrawalDto> RequestWithdrawalAsync(int walletId, decimal amount);
    Task ApproveWithdrawalAsync(int id, string? remarks);
    Task RejectWithdrawalAsync(int id, string? remarks);
}