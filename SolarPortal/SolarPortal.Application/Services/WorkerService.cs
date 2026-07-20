using AutoMapper;
using SolarPortal.Application.DTOs;
using SolarPortal.Application.Interfaces;
using SolarPortal.Application.Interfaces.Services;
using SolarPortal.Domain.Entities;

namespace SolarPortal.Application.Services;

public class WorkerService : IWorkerService
{
    private readonly IUnitOfWork _uow;
    private readonly IMapper _mapper;

    public WorkerService(IUnitOfWork uow, IMapper mapper)
    {
        _uow = uow;
        _mapper = mapper;
    }

    public async Task<IEnumerable<WorkerDto>> GetAllAsync()
    {
        var workers = await _uow.Workers.GetAllAsync();
        return _mapper.Map<IEnumerable<WorkerDto>>(workers);
    }

    public async Task<WorkerDto?> GetByIdAsync(int id)
    {
        var worker = await _uow.Workers.GetByIdAsync(id);
        return worker == null ? null : _mapper.Map<WorkerDto>(worker);
    }

    public async Task<WorkerDto> CreateAsync(CreateWorkerDto dto)
    {
        var worker = _mapper.Map<Worker>(dto);
        await _uow.Workers.AddAsync(worker);
        await _uow.SaveChangesAsync();
        return _mapper.Map<WorkerDto>(worker);
    }

    public async Task<bool> LoginUsernameExistsAsync(string username, int? excludeWorkerId = null)
    {
        if (string.IsNullOrWhiteSpace(username)) return false;
        var workers = await _uow.Workers.GetAllAsync();
        return workers.Any(w =>
            w.Id != excludeWorkerId &&
            string.Equals(w.LoginUsername, username, StringComparison.OrdinalIgnoreCase));
    }

    public async Task DeleteAsync(int id)
    {
        var worker = await _uow.Workers.GetByIdAsync(id);
        if (worker != null)
        {
            worker.IsDeleted = true;
            _uow.Workers.Update(worker);
            await _uow.SaveChangesAsync();
        }
    }

    public async Task ToggleAvailabilityAsync(int id)
    {
        var worker = await _uow.Workers.GetByIdAsync(id);
        if (worker != null)
        {
            worker.IsAvailable = !worker.IsAvailable;
            _uow.Workers.Update(worker);
            await _uow.SaveChangesAsync();
        }
    }

    public async Task<bool> AssignToInstallationAsync(int installationId, int workerId)
    {
        var assignment = new WorkerAssignment
        {
            InstallationId = installationId,
            WorkerId = workerId,
            AssignedByUserId = "system",
            AssignedDate = DateTime.UtcNow
        };
        await _uow.WorkerAssignments.AddAsync(assignment);
        await _uow.SaveChangesAsync();
        return true;
    }
}