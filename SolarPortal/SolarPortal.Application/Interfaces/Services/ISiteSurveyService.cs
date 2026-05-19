using SolarPortal.Application.DTOs;

namespace SolarPortal.Application.Interfaces.Services;

public interface ISiteSurveyService
{
    Task<SiteSurveyDto?> GetByIdAsync(int id);
    Task<IEnumerable<SiteSurveyDto>> GetBySolarRequestIdAsync(int solarRequestId);
    Task<IEnumerable<SiteSurveyDto>> GetByEngineerIdAsync(string engineerId);
    Task<SiteSurveyDto> CreateAsync(int solarRequestId, string assignedToUserId);
    Task<SiteSurveyDto> UpdateAsync(int id, SiteSurveyDto dto);
    Task<SiteSurveyDto> CompleteSurveyAsync(int id, string surveyNotes, string? photoPath);
    Task DeleteAsync(int id);
}
