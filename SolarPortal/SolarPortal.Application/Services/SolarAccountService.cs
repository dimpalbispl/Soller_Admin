using AutoMapper;
using SolarPortal.Application.DTOs;
using SolarPortal.Application.Interfaces;
using SolarPortal.Application.Interfaces.Services;
using SolarPortal.Domain.Entities;
using SolarPortal.Domain.Enums;

namespace SolarPortal.Application.Services;

public class SolarAccountService : ISolarAccountService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IMapper _mapper;

    public SolarAccountService(IUnitOfWork unitOfWork, IMapper mapper)
    {
        _unitOfWork = unitOfWork;
        _mapper = mapper;
    }

    public async Task<SolarAccountDto?> GetByIdAsync(int id)
    {
        var account = await _unitOfWork.SolarAccounts.GetByIdAsync(id);
        return account != null ? _mapper.Map<SolarAccountDto>(account) : null;
    }

    public async Task<SolarAccountDto?> GetByRequestIdAsync(int requestId)
    {
        var account = await _unitOfWork.SolarAccounts.FindAsync(a => a.SolarRequestId == requestId);
        return account != null ? _mapper.Map<SolarAccountDto>(account.FirstOrDefault()) : null;
    }

    public async Task<SolarAccountDto?> GetByAccountNumberAsync(string accountNumber)
    {
        var account = await _unitOfWork.SolarAccounts.FindAsync(a => a.AccountNumber == accountNumber);
        return account != null ? _mapper.Map<SolarAccountDto>(account.FirstOrDefault()) : null;
    }

    public async Task<IEnumerable<SolarAccountDto>> GetByUserIdAsync(string userId)
    {
        var accounts = await _unitOfWork.SolarAccounts.FindAsync(a => a.UserId == userId);
        return _mapper.Map<IEnumerable<SolarAccountDto>>(accounts);
    }

    public async Task<SolarAccountDto> CreateAsync(int solarRequestId)
    {
        var request = await _unitOfWork.SolarRequests.GetByIdAsync(solarRequestId);
        if (request == null) throw new ArgumentException("Solar request not found");

        var accountNumber = $"SA-{solarRequestId:D4}";
        var account = new SolarAccount
        {
            AccountNumber = accountNumber,
            UserId = request.UserId,
            SolarRequestId = solarRequestId,
            ProjectAmount = request.PlanAmount,
            DepositAmount = 0,
            DueAmount = request.PlanAmount,
            CurrentStatus = request.CurrentStage
        };

        await _unitOfWork.SolarAccounts.AddAsync(account);
        await _unitOfWork.SaveChangesAsync();

        return _mapper.Map<SolarAccountDto>(account);
    }

    public async Task UpdateStatusAsync(int id, ProjectStatus status)
    {
        var account = await _unitOfWork.SolarAccounts.GetByIdAsync(id);
        if (account == null) throw new ArgumentException("Solar account not found");

        account.CurrentStatus = status;
        account.UpdatedAt = DateTime.UtcNow;

        _unitOfWork.SolarAccounts.Update(account);
        await _unitOfWork.SaveChangesAsync();
    }

    public async Task FreezeAccountAsync(int id, bool freeze)
    {
        var account = await _unitOfWork.SolarAccounts.GetByIdAsync(id);
        if (account == null) throw new ArgumentException("Solar account not found");

        account.IsFrozen = freeze;
        account.UpdatedAt = DateTime.UtcNow;

        _unitOfWork.SolarAccounts.Update(account);
        await _unitOfWork.SaveChangesAsync();
    }

    public async Task UpdateAmountsAsync(int id, decimal depositAmount, decimal dueAmount)
    {
        var account = await _unitOfWork.SolarAccounts.GetByIdAsync(id);
        if (account == null) throw new ArgumentException("Solar account not found");

        account.DepositAmount = depositAmount;
        account.DueAmount = dueAmount;
        account.UpdatedAt = DateTime.UtcNow;

        _unitOfWork.SolarAccounts.Update(account);
        await _unitOfWork.SaveChangesAsync();
    }
}