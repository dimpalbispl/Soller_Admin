using AutoMapper;
using SolarPortal.Application.DTOs;
using SolarPortal.Application.Interfaces;
using SolarPortal.Application.Interfaces.Services;
using SolarPortal.Domain.Entities;

namespace SolarPortal.Application.Services;

public class SolarProjectService : ISolarProjectService
{
    private readonly IUnitOfWork _uow;
    private readonly IMapper _mapper;

    public SolarProjectService(IUnitOfWork uow, IMapper mapper)
    {
        _uow = uow;
        _mapper = mapper;
    }

    public async Task<IEnumerable<SolarProjectDto>> GetAllAsync(bool activeOnly = false)
    {
        var items = activeOnly
            ? await _uow.SolarProjects.FindAsync(p => p.IsActive)
            : await _uow.SolarProjects.GetAllAsync();
        return _mapper.Map<IEnumerable<SolarProjectDto>>(
            items.OrderBy(p => p.SolarTypeKV).ThenBy(p => p.TotalAmount));
    }

    public async Task<SolarProjectDto?> GetByIdAsync(int id)
    {
        var item = await _uow.SolarProjects.GetByIdAsync(id);
        return item == null ? null : _mapper.Map<SolarProjectDto>(item);
    }

    public async Task<ServiceResult<SolarProjectDto>> CreateAsync(CreateSolarProjectDto dto)
    {
        try
        {
            var entity = _mapper.Map<SolarProject>(dto);
            if (entity.TotalAmount <= 0)
                entity.TotalAmount = dto.DiscomWork + dto.DealClose + dto.SCZMenue + dto.SportainTeam;

            await _uow.SolarProjects.AddAsync(entity);
            await _uow.SaveChangesAsync();

            return ServiceResult<SolarProjectDto>.Success(
                _mapper.Map<SolarProjectDto>(entity), "Solar project created");
        }
        catch (Exception ex)
        {
            return ServiceResult<SolarProjectDto>.Failure($"Failed to create: {ex.Message}");
        }
    }

    public async Task<ServiceResult<bool>> ToggleActiveAsync(int id)
    {
        var entity = await _uow.SolarProjects.GetByIdAsync(id);
        if (entity == null) return ServiceResult<bool>.Failure("Not found");

        entity.IsActive = !entity.IsActive;
        _uow.SolarProjects.Update(entity);
        await _uow.SaveChangesAsync();
        return ServiceResult<bool>.Success(true, "Status updated");
    }

    public async Task<ServiceResult<bool>> DeleteAsync(int id)
    {
        var entity = await _uow.SolarProjects.GetByIdAsync(id);
        if (entity == null) return ServiceResult<bool>.Failure("Not found");

        entity.IsDeleted = true;
        _uow.SolarProjects.Update(entity);
        await _uow.SaveChangesAsync();
        return ServiceResult<bool>.Success(true, "Deleted");
    }
}
