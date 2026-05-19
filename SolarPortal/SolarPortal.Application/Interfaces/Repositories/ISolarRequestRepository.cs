using SolarPortal.Domain.Entities;

namespace SolarPortal.Application.Interfaces.Repositories;

public interface ISolarRequestRepository : IGenericRepository<SolarRequest>
{
    Task<SolarRequest?> GetByRequestNumberAsync(string requestNumber);
    Task<IEnumerable<SolarRequest>> GetByUserIdAsync(string userId);
    Task<IEnumerable<SolarRequest>> GetPendingApprovalsAsync();
    Task<string> GenerateRequestNumberAsync();
    Task<SolarRequest?> GetWithDetailsAsync(int id);
    Task<Dictionary<string, int>> GetStatusCountsAsync();
}