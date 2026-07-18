using SolarPortal.Domain.Enums;

namespace SolarPortal.Application.DTOs;

public class WorkerDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string MobileNumber { get; set; } = string.Empty;
    public string Specialization { get; set; } = string.Empty;
    public WorkerType Type { get; set; }
    public bool IsAvailable { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
    public int AssignmentsCount { get; set; }

    // INC panel info (Phase 1): shown in the admin Workers list.
    public string? LoginUsername { get; set; }
    public string? LoginPassword { get; set; }
    public decimal? CommissionPercent { get; set; }
}

public class CreateWorkerDto
{
    public string Name { get; set; } = string.Empty;
    public string MobileNumber { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string Specialization { get; set; } = string.Empty;
    public WorkerType Type { get; set; } = WorkerType.JOB;
    public string? City { get; set; }
    public string? State { get; set; }
    public bool IsAvailable { get; set; } = true;

    // INC-only login + commission (Phase 1)
    public string? LoginUsername { get; set; }
    public string? LoginPassword { get; set; }
    public decimal? CommissionPercent { get; set; }
}

public class CreateNotificationDto
{
    public string UserId { get; set; } = string.Empty;
    public int? SolarRequestId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string? NotificationType { get; set; }
}