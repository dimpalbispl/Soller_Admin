using System.ComponentModel.DataAnnotations;
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

    // Panel login for BOTH worker types (JOB and INC). Commission is INC-only.
    [Required(ErrorMessage = "Username is required.")]
    [StringLength(50, MinimumLength = 3, ErrorMessage = "Username must be 3-50 characters.")]
    public string? LoginUsername { get; set; }

    [Required(ErrorMessage = "Password is required.")]
    [StringLength(100, MinimumLength = 4, ErrorMessage = "Password must be at least 4 characters.")]
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