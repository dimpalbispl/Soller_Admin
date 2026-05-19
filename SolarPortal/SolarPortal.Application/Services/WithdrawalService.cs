using AutoMapper;
using SolarPortal.Application.DTOs;
using SolarPortal.Application.Interfaces;
using SolarPortal.Application.Interfaces.Services;
using SolarPortal.Domain.Entities;
using SolarPortal.Domain.Enums;

namespace SolarPortal.Application.Services;

public class WithdrawalService : IWithdrawalService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IMapper _mapper;

    public WithdrawalService(IUnitOfWork unitOfWork, IMapper mapper)
    {
        _unitOfWork = unitOfWork;
        _mapper = mapper;
    }

    public async Task<WithdrawalDto?> GetByIdAsync(int id)
    {
        var withdrawal = await _unitOfWork.Withdrawals.GetByIdAsync(id);
        return withdrawal != null ? _mapper.Map<WithdrawalDto>(withdrawal) : null;
    }

    public async Task<IEnumerable<WithdrawalDto>> GetByWalletIdAsync(int walletId)
    {
        var withdrawals = await _unitOfWork.Withdrawals.FindAsync(w => w.WalletId == walletId);
        return _mapper.Map<IEnumerable<WithdrawalDto>>(withdrawals);
    }

    public async Task<WithdrawalDto> RequestWithdrawalAsync(int walletId, decimal amount)
    {
        var wallet = await _unitOfWork.Wallets.GetByIdAsync(walletId);
        if (wallet == null) throw new ArgumentException("Wallet not found");

        if (amount > wallet.PendingBalance) throw new ArgumentException("Insufficient balance");

        var withdrawal = new Withdrawal
        {
            WalletId = walletId,
            Amount = amount
        };

        await _unitOfWork.Withdrawals.AddAsync(withdrawal);
        await _unitOfWork.SaveChangesAsync();

        return _mapper.Map<WithdrawalDto>(withdrawal);
    }

    public async Task ApproveWithdrawalAsync(int id, string? remarks)
    {
        var withdrawal = await _unitOfWork.Withdrawals.GetByIdAsync(id);
        if (withdrawal == null) throw new ArgumentException("Withdrawal not found");

        withdrawal.Status = ApprovalStatus.Approved;
        withdrawal.Remarks = remarks;
        withdrawal.ProcessedDate = DateTime.UtcNow;
        withdrawal.UpdatedAt = DateTime.UtcNow;

        _unitOfWork.Withdrawals.Update(withdrawal);
        await _unitOfWork.SaveChangesAsync();

        // Update wallet
        var wallet = await _unitOfWork.Wallets.GetByIdAsync(withdrawal.WalletId);
        if (wallet != null)
        {
            wallet.WithdrawnAmount += withdrawal.Amount;
            wallet.PendingBalance -= withdrawal.Amount;
            wallet.NetAmount = wallet.TotalIncome - wallet.TDS - wallet.WithdrawnAmount;
            wallet.UpdatedAt = DateTime.UtcNow;
            _unitOfWork.Wallets.Update(wallet);
            await _unitOfWork.SaveChangesAsync();
        }
    }

    public async Task RejectWithdrawalAsync(int id, string? remarks)
    {
        var withdrawal = await _unitOfWork.Withdrawals.GetByIdAsync(id);
        if (withdrawal == null) throw new ArgumentException("Withdrawal not found");

        withdrawal.Status = ApprovalStatus.Rejected;
        withdrawal.Remarks = remarks;
        withdrawal.ProcessedDate = DateTime.UtcNow;
        withdrawal.UpdatedAt = DateTime.UtcNow;

        _unitOfWork.Withdrawals.Update(withdrawal);
        await _unitOfWork.SaveChangesAsync();
    }
}