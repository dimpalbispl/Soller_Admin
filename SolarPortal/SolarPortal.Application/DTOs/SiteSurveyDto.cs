namespace SolarPortal.Application.DTOs;

public class SiteSurveyDto
{
    public int Id { get; set; }
    public int SolarRequestId { get; set; }
    public string? SolarRequestNumber { get; set; }
    public string? AssignedToUserId { get; set; }
    public string? AssignedToUserName { get; set; }
    public DateTime? SurveyDate { get; set; }
    public string? SurveyNotes { get; set; }
    public string? SurveyPhotoPath { get; set; }
    public bool IsCompleted { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
