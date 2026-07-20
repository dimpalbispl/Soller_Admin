using SolarPortal.Application.DTOs;

namespace SolarPortal.Application.Interfaces.Services;

public interface IWorkerService
{
    Task<IEnumerable<WorkerDto>> GetAllAsync();
    Task<WorkerDto?> GetByIdAsync(int id);
    Task<WorkerDto> CreateAsync(CreateWorkerDto dto);
    Task DeleteAsync(int id);
    Task ToggleAvailabilityAsync(int id);
    Task<bool> AssignToInstallationAsync(int installationId, int workerId);

    /// <summary>
    /// True if another worker already uses this login username. Credentials are
    /// per-worker, so duplicates would make the login ambiguous.
    /// </summary>
    Task<bool> LoginUsernameExistsAsync(string username, int? excludeWorkerId = null);
}