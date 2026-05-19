using Microsoft.EntityFrameworkCore;
using SolarPortal.Application.Interfaces.Repositories;
using SolarPortal.Domain.Entities;
using SolarPortal.Domain.Enums;
using SolarPortal.Infrastructure.Data;

namespace SolarPortal.Infrastructure.Repositories;

public class SolarRequestRepository : GenericRepository<SolarRequest>, ISolarRequestRepository
{
    public SolarRequestRepository(ApplicationDbContext context) : base(context) { }

    public async Task<SolarRequest?> GetByRequestNumberAsync(string requestNumber) =>
        await _dbSet.FirstOrDefaultAsync(x => x.RequestNumber == requestNumber);

    public async Task<IEnumerable<SolarRequest>> GetByUserIdAsync(string userId) =>
        await _dbSet
            .Include(x => x.Payments)
            .Include(x => x.Documents)
            .Where(x => x.UserId == userId)
            .OrderByDescending(x => x.CreatedAt)
            .ToListAsync();

    public async Task<IEnumerable<SolarRequest>> GetPendingApprovalsAsync() =>
        await _dbSet
            .Include(x => x.User)
            .Include(x => x.Payments)
            .Where(x => x.ApprovalStatus == ApprovalStatus.Pending)
            .OrderByDescending(x => x.CreatedAt)
            .ToListAsync();

    public async Task<string> GenerateRequestNumberAsync()
    {
        int count = await _dbSet.CountAsync() + 1;
        return $"SCR-{count:D3}";
    }

    public async Task<SolarRequest?> GetWithDetailsAsync(int id) =>
        await _dbSet
            .Include(x => x.User)
            .Include(x => x.Payments)
            .Include(x => x.Documents)
            .Include(x => x.SiteSurveys).ThenInclude(s => s.AssignedTo)
            .Include(x => x.MeterDispatches)
            .Include(x => x.MaterialDispatches)
            .Include(x => x.Installations).ThenInclude(i => i.WorkerAssignments).ThenInclude(wa => wa.Worker)
            .Include(x => x.DCRDocuments)
            .Include(x => x.Commission)
            .FirstOrDefaultAsync(x => x.Id == id);

    public async Task<Dictionary<string, int>> GetStatusCountsAsync()
    {
        return await _dbSet
            .GroupBy(x => x.CurrentStage)
            .ToDictionaryAsync(
                g => g.Key.ToString(),
                g => g.Count());
    }
}