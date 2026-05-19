using SolarPortal.Application.DTOs;

namespace SolarPortal.Application.Interfaces.Services;

public interface ISolarProjectService
{
    Task<IEnumerable<SolarProjectDto>> GetAllAsync(bool activeOnly = false);
    Task<SolarProjectDto?> GetByIdAsync(int id);
    Task<ServiceResult<SolarProjectDto>> CreateAsync(CreateSolarProjectDto dto);
    Task<ServiceResult<bool>> ToggleActiveAsync(int id);
    Task<ServiceResult<bool>> DeleteAsync(int id);
}
