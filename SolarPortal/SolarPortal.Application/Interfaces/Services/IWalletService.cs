using SolarPortal.Application.DTOs;

namespace SolarPortal.Application.Interfaces.Services;

public interface IWalletService
{
    Task<WalletDto?> GetByUserIdAsync(string userId);
    Task<WalletDto> CreateAsync(string userId);
    Task UpdateIncomeAsync(string userId, decimal incomeAmount);
    Task ProcessTDSAsync(string userId, decimal tdsAmount);
    Task WithdrawAsync(string userId, decimal amount);
}