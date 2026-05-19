using SolarPortal.Application.DTOs;

namespace SolarPortal.Application.Interfaces.Services;

public interface ISolarRequestService
{
    Task<ServiceResult<SolarRequestDto>> CreateAsync(CreateSolarRequestDto dto, string userId);
    Task<ServiceResult<SolarRequestDto>> GetByIdAsync(int id);
    Task<ServiceResult<SolarRequestDto>> GetByRequestNumberAsync(string requestNumber);
    Task<ServiceResult<IEnumerable<SolarRequestDto>>> GetByUserIdAsync(string userId);
    Task<ServiceResult<IEnumerable<SolarRequestDto>>> GetAllAsync(int page = 1, int pageSize = 20);
    Task<ServiceResult<IEnumerable<SolarRequestDto>>> GetPendingApprovalsAsync();
    Task<ServiceResult<bool>> ApproveAsync(int id, string adminId, string? notes = null);
    Task<ServiceResult<bool>> RejectAsync(int id, string adminId, string reason);
    Task<ServiceResult<bool>> UpdateStageAsync(UpdateSolarRequestStatusDto dto, string adminId);
    Task<ServiceResult<SolarRequestDto>> GetWithDetailsAsync(int id);
}