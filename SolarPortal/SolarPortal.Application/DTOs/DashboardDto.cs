namespace SolarPortal.Application.DTOs;

public class AdminDashboardDto
{
    public int TotalProjects { get; set; }
    public int PendingApprovals { get; set; }
    public int ActiveInstallations { get; set; }
    public int CompletedProjects { get; set; }
    public decimal TotalRevenue { get; set; }
    public decimal PendingPayments { get; set; }
    public int TotalWorkers { get; set; }
    public List<SolarRequestDto> RecentRequests { get; set; } = new();
    public Dictionary<string, int> StatusDistribution { get; set; } = new();
}

public class UserDashboardDto
{
    public int TotalProjects { get; set; }
    public int PendingApprovals { get; set; }
    public decimal TotalPaid { get; set; }
    public decimal TotalDue { get; set; }
    public List<SolarRequestDto> MyProjects { get; set; } = new();
    public SolarRequestDto? LatestProject { get; set; }
    public List<NotificationDto> UnreadNotifications { get; set; } = new();
    public List<SiteSurveyDto> MySiteSurveys { get; set; } = new();
    public int PendingSurveys { get; set; }
    public int CompletedSurveys { get; set; }
}

public class NotificationDto
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public bool IsRead { get; set; }
    public string? NotificationType { get; set; }
    public DateTime CreatedAt { get; set; }
    public int? SolarRequestId { get; set; }
}