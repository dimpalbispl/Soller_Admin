using AutoMapper;
using SolarPortal.Application.DTOs;
using SolarPortal.Application.Interfaces;
using SolarPortal.Application.Interfaces.Services;
using SolarPortal.Domain.Entities;

namespace SolarPortal.Application.Services;

public class SiteSurveyService : ISiteSurveyService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IMapper _mapper;

    public SiteSurveyService(IUnitOfWork unitOfWork, IMapper mapper)
    {
        _unitOfWork = unitOfWork;
        _mapper = mapper;
    }

    public async Task<SiteSurveyDto?> GetByIdAsync(int id)
    {
        var survey = await _unitOfWork.SiteSurveys.GetByIdAsync(id);
        if (survey == null) return null;

        var dto = _mapper.Map<SiteSurveyDto>(survey);
        if (survey.SolarRequest != null)
            dto.SolarRequestNumber = survey.SolarRequest.RequestNumber;
        if (survey.AssignedTo != null)
            dto.AssignedToUserName = survey.AssignedTo.FullName;

        return dto;
    }

    public async Task<IEnumerable<SiteSurveyDto>> GetBySolarRequestIdAsync(int solarRequestId)
    {
        var surveys = await _unitOfWork.SiteSurveys.FindAsync(s => s.SolarRequestId == solarRequestId);
        var dtos = _mapper.Map<List<SiteSurveyDto>>(surveys);
        
        foreach (var dto in dtos)
        {
            var survey = surveys.FirstOrDefault(s => s.Id == dto.Id);
            if (survey?.SolarRequest != null)
                dto.SolarRequestNumber = survey.SolarRequest.RequestNumber;
            if (survey?.AssignedTo != null)
                dto.AssignedToUserName = survey.AssignedTo.FullName;
        }

        return dtos;
    }

    public async Task<IEnumerable<SiteSurveyDto>> GetByEngineerIdAsync(string engineerId)
    {
        var surveys = await _unitOfWork.SiteSurveys.FindAsync(s => s.AssignedToUserId == engineerId);
        var dtos = _mapper.Map<List<SiteSurveyDto>>(surveys);
        
        foreach (var dto in dtos)
        {
            var survey = surveys.FirstOrDefault(s => s.Id == dto.Id);
            if (survey?.SolarRequest != null)
                dto.SolarRequestNumber = survey.SolarRequest.RequestNumber;
        }

        return dtos;
    }

    public async Task<SiteSurveyDto> CreateAsync(int solarRequestId, string assignedToUserId)
    {
        var request = await _unitOfWork.SolarRequests.GetByIdAsync(solarRequestId);
        if (request == null)
            throw new ArgumentException("Solar request not found");

        var existingSurvey = (await _unitOfWork.SiteSurveys
            .FindAsync(s => s.SolarRequestId == solarRequestId))
            .FirstOrDefault();

        if (existingSurvey != null)
            throw new ArgumentException("Survey already exists for this request");

        var survey = new SiteSurvey
        {
            SolarRequestId = solarRequestId,
            AssignedToUserId = assignedToUserId,
            IsCompleted = false,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        await _unitOfWork.SiteSurveys.AddAsync(survey);
        await _unitOfWork.SaveChangesAsync();

        return await GetByIdAsync(survey.Id) ?? new SiteSurveyDto();
    }

    public async Task<SiteSurveyDto> UpdateAsync(int id, SiteSurveyDto dto)
    {
        var survey = await _unitOfWork.SiteSurveys.GetByIdAsync(id);
        if (survey == null)
            throw new ArgumentException("Site survey not found");

        survey.SurveyDate = dto.SurveyDate ?? survey.SurveyDate;
        survey.SurveyNotes = dto.SurveyNotes ?? survey.SurveyNotes;
        survey.SurveyPhotoPath = dto.SurveyPhotoPath ?? survey.SurveyPhotoPath;
        survey.UpdatedAt = DateTime.UtcNow;

        _unitOfWork.SiteSurveys.Update(survey);
        await _unitOfWork.SaveChangesAsync();

        return await GetByIdAsync(id) ?? new SiteSurveyDto();
    }

    public async Task<SiteSurveyDto> CompleteSurveyAsync(int id, string surveyNotes, string? photoPath)
    {
        var survey = await _unitOfWork.SiteSurveys.GetByIdAsync(id);
        if (survey == null)
            throw new ArgumentException("Site survey not found");

        survey.IsCompleted = true;
        survey.CompletedAt = DateTime.UtcNow;
        survey.SurveyNotes = surveyNotes;
        if (photoPath != null)
            survey.SurveyPhotoPath = photoPath;
        survey.UpdatedAt = DateTime.UtcNow;

        _unitOfWork.SiteSurveys.Update(survey);
        await _unitOfWork.SaveChangesAsync();

        return await GetByIdAsync(id) ?? new SiteSurveyDto();
    }

    public async Task DeleteAsync(int id)
    {
        var survey = await _unitOfWork.SiteSurveys.GetByIdAsync(id);
        if (survey == null)
            throw new ArgumentException("Site survey not found");

        _unitOfWork.SiteSurveys.Remove(survey);
        await _unitOfWork.SaveChangesAsync();
    }
}
