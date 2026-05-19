using SolarPortal.Application.DTOs;
using SolarPortal.Domain.Entities;
using SolarPortal.Domain.Enums;

namespace SolarPortal.Application.Interfaces.Services;

public interface ISolarAccountService
{
    Task<SolarAccountDto?> GetByIdAsync(int id);
    Task<SolarAccountDto?> GetByRequestIdAsync(int requestId);
    Task<SolarAccountDto?> GetByAccountNumberAsync(string accountNumber);
    Task<IEnumerable<SolarAccountDto>> GetByUserIdAsync(string userId);
    Task<SolarAccountDto> CreateAsync(int solarRequestId);
    Task UpdateStatusAsync(int id, ProjectStatus status);
    Task FreezeAccountAsync(int id, bool freeze);
    Task UpdateAmountsAsync(int id, decimal depositAmount, decimal dueAmount);
}