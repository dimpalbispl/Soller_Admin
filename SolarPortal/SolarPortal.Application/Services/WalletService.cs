using AutoMapper;
using SolarPortal.Application.DTOs;
using SolarPortal.Application.Interfaces;
using SolarPortal.Application.Interfaces.Services;
using SolarPortal.Domain.Entities;

namespace SolarPortal.Application.Services;

public class WalletService : IWalletService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IMapper _mapper;

    public WalletService(IUnitOfWork unitOfWork, IMapper mapper)
    {
        _unitOfWork = unitOfWork;
        _mapper = mapper;
    }

    public async Task<WalletDto?> GetByUserIdAsync(string userId)
    {
        var wallet = await _unitOfWork.Wallets.FindAsync(w => w.UserId == userId);
        return wallet != null ? _mapper.Map<WalletDto>(wallet.FirstOrDefault()) : null;
    }

    public async Task<WalletDto> CreateAsync(string userId)
    {
        var wallet = new Wallet
        {
            UserId = userId
        };

        await _unitOfWork.Wallets.AddAsync(wallet);
        await _unitOfWork.SaveChangesAsync();

        return _mapper.Map<WalletDto>(wallet);
    }

    public async Task UpdateIncomeAsync(string userId, decimal incomeAmount)
    {
        var wallets = await _unitOfWork.Wallets.FindAsync(w => w.UserId == userId);
        var wallet = wallets.FirstOrDefault();
        if (wallet == null)
        {
            wallet = new Wallet { UserId = userId };
            await _unitOfWork.Wallets.AddAsync(wallet);
        }

        wallet.TotalIncome += incomeAmount;
        var tds = incomeAmount * 0.01m; // 1% TDS
        wallet.TDS += tds;
        wallet.NetAmount = wallet.TotalIncome - wallet.TDS - wallet.WithdrawnAmount;
        wallet.PendingBalance = wallet.NetAmount;
        wallet.UpdatedAt = DateTime.UtcNow;

        _unitOfWork.Wallets.Update(wallet);
        await _unitOfWork.SaveChangesAsync();
    }

    public async Task ProcessTDSAsync(string userId, decimal tdsAmount)
    {
        var wallets = await _unitOfWork.Wallets.FindAsync(w => w.UserId == userId);
        var wallet = wallets.FirstOrDefault();
        if (wallet == null) throw new ArgumentException("Wallet not found");

        wallet.TDS += tdsAmount;
        wallet.NetAmount = wallet.TotalIncome - wallet.TDS - wallet.WithdrawnAmount;
        wallet.PendingBalance = wallet.NetAmount;
        wallet.UpdatedAt = DateTime.UtcNow;

        _unitOfWork.Wallets.Update(wallet);
        await _unitOfWork.SaveChangesAsync();
    }

    public async Task WithdrawAsync(string userId, decimal amount)
    {
        var wallets = await _unitOfWork.Wallets.FindAsync(w => w.UserId == userId);
        var wallet = wallets.FirstOrDefault();
        if (wallet == null) throw new ArgumentException("Wallet not found");

        if (amount > wallet.PendingBalance) throw new ArgumentException("Insufficient balance");

        wallet.WithdrawnAmount += amount;
        wallet.PendingBalance -= amount;
        wallet.NetAmount = wallet.TotalIncome - wallet.TDS - wallet.WithdrawnAmount;
        wallet.UpdatedAt = DateTime.UtcNow;

        _unitOfWork.Wallets.Update(wallet);
        await _unitOfWork.SaveChangesAsync();
    }
}