using SolarPortal.Application.Interfaces.Repositories;

namespace SolarPortal.Application.Interfaces;

public interface IUnitOfWork : IDisposable
{
    ISolarRequestRepository SolarRequests { get; }
    IGenericRepository<Domain.Entities.Payment> Payments { get; }
    IGenericRepository<Domain.Entities.Document> Documents { get; }
    IGenericRepository<Domain.Entities.Worker> Workers { get; }
    IGenericRepository<Domain.Entities.SiteSurvey> SiteSurveys { get; }
    IGenericRepository<Domain.Entities.MeterDispatch> MeterDispatches { get; }
    IGenericRepository<Domain.Entities.MaterialDispatch> MaterialDispatches { get; }
    IGenericRepository<Domain.Entities.Installation> Installations { get; }
    IGenericRepository<Domain.Entities.WorkerAssignment> WorkerAssignments { get; }
    IGenericRepository<Domain.Entities.DCRDocument> DCRDocuments { get; }
    IGenericRepository<Domain.Entities.Commission> Commissions { get; }
    IGenericRepository<Domain.Entities.Notification> Notifications { get; }
    IGenericRepository<Domain.Entities.SolarProject> SolarProjects { get; }
    IGenericRepository<Domain.Entities.SolarAccount> SolarAccounts { get; }
    IGenericRepository<Domain.Entities.PMDocument> PMDocuments { get; }
    IGenericRepository<Domain.Entities.Wallet> Wallets { get; }
    IGenericRepository<Domain.Entities.WalletTransaction> WalletTransactions { get; }
    IGenericRepository<Domain.Entities.Withdrawal> Withdrawals { get; }
    IGenericRepository<Domain.Entities.ActivityLog> ActivityLogs { get; }
    Task<int> SaveChangesAsync();
    Task BeginTransactionAsync();
    Task CommitTransactionAsync();
    Task RollbackTransactionAsync();
}