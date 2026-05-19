using Microsoft.EntityFrameworkCore.Storage;
using SolarPortal.Application.Interfaces;
using SolarPortal.Application.Interfaces.Repositories;
using SolarPortal.Domain.Entities;
using SolarPortal.Infrastructure.Data;
using SolarPortal.Infrastructure.Repositories;

namespace SolarPortal.Infrastructure;

public class UnitOfWork : IUnitOfWork
{
    private readonly ApplicationDbContext _context;
    private IDbContextTransaction? _transaction;

    private ISolarRequestRepository? _solarRequests;
    private IGenericRepository<Payment>? _payments;
    private IGenericRepository<Document>? _documents;
    private IGenericRepository<Worker>? _workers;
    private IGenericRepository<SiteSurvey>? _siteSurveys;
    private IGenericRepository<MeterDispatch>? _meterDispatches;
    private IGenericRepository<MaterialDispatch>? _materialDispatches;
    private IGenericRepository<Installation>? _installations;
    private IGenericRepository<WorkerAssignment>? _workerAssignments;
    private IGenericRepository<DCRDocument>? _dcrDocuments;
    private IGenericRepository<Commission>? _commissions;
    private IGenericRepository<Notification>? _notifications;
    private IGenericRepository<SolarProject>? _solarProjects;
    private IGenericRepository<SolarAccount>? _solarAccounts;
    private IGenericRepository<PMDocument>? _pmDocuments;
    private IGenericRepository<Wallet>? _wallets;
    private IGenericRepository<WalletTransaction>? _walletTransactions;
    private IGenericRepository<Withdrawal>? _withdrawals;
    private IGenericRepository<ActivityLog>? _activityLogs;

    public UnitOfWork(ApplicationDbContext context) => _context = context;

    public ISolarRequestRepository SolarRequests =>
        _solarRequests ??= new SolarRequestRepository(_context);
    public IGenericRepository<Payment> Payments =>
        _payments ??= new GenericRepository<Payment>(_context);
    public IGenericRepository<Document> Documents =>
        _documents ??= new GenericRepository<Document>(_context);
    public IGenericRepository<Worker> Workers =>
        _workers ??= new GenericRepository<Worker>(_context);
    public IGenericRepository<SiteSurvey> SiteSurveys =>
        _siteSurveys ??= new GenericRepository<SiteSurvey>(_context);
    public IGenericRepository<MeterDispatch> MeterDispatches =>
        _meterDispatches ??= new GenericRepository<MeterDispatch>(_context);
    public IGenericRepository<MaterialDispatch> MaterialDispatches =>
        _materialDispatches ??= new GenericRepository<MaterialDispatch>(_context);
    public IGenericRepository<Installation> Installations =>
        _installations ??= new GenericRepository<Installation>(_context);
    public IGenericRepository<WorkerAssignment> WorkerAssignments =>
        _workerAssignments ??= new GenericRepository<WorkerAssignment>(_context);
    public IGenericRepository<DCRDocument> DCRDocuments =>
        _dcrDocuments ??= new GenericRepository<DCRDocument>(_context);
    public IGenericRepository<Commission> Commissions =>
        _commissions ??= new GenericRepository<Commission>(_context);
    public IGenericRepository<Notification> Notifications =>
        _notifications ??= new GenericRepository<Notification>(_context);
    public IGenericRepository<SolarProject> SolarProjects =>
        _solarProjects ??= new GenericRepository<SolarProject>(_context);
    public IGenericRepository<SolarAccount> SolarAccounts =>
        _solarAccounts ??= new GenericRepository<SolarAccount>(_context);
    public IGenericRepository<PMDocument> PMDocuments =>
        _pmDocuments ??= new GenericRepository<PMDocument>(_context);
    public IGenericRepository<Wallet> Wallets =>
        _wallets ??= new GenericRepository<Wallet>(_context);
    public IGenericRepository<WalletTransaction> WalletTransactions =>
        _walletTransactions ??= new GenericRepository<WalletTransaction>(_context);
    public IGenericRepository<Withdrawal> Withdrawals =>
        _withdrawals ??= new GenericRepository<Withdrawal>(_context);
    public IGenericRepository<ActivityLog> ActivityLogs =>
        _activityLogs ??= new GenericRepository<ActivityLog>(_context);

    public async Task<int> SaveChangesAsync() =>
        await _context.SaveChangesAsync();

    public async Task BeginTransactionAsync() =>
        _transaction = await _context.Database.BeginTransactionAsync();

    public async Task CommitTransactionAsync()
    {
        if (_transaction != null)
        {
            await _transaction.CommitAsync();
            await _transaction.DisposeAsync();
            _transaction = null;
        }
    }

    public async Task RollbackTransactionAsync()
    {
        if (_transaction != null)
        {
            await _transaction.RollbackAsync();
            await _transaction.DisposeAsync();
            _transaction = null;
        }
    }

    public void Dispose()
    {
        _transaction?.Dispose();
        _context.Dispose();
    }
}