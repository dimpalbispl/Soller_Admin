using SolarPortal.Domain.Enums;

namespace SolarPortal.Application.DTOs;

public class SolarRequestDto
{
    public int Id { get; set; }
    public string RequestNumber { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string ApplicantName { get; set; } = string.Empty;
    public string MobileNumber { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string PinCode { get; set; } = string.Empty;
    public string? AadharNumber { get; set; }
    public string? PANNumber { get; set; }
    public RequestType RequestType { get; set; }
    public ConnectionType ConnectionType { get; set; }
    public decimal KVCapacity { get; set; }
    public int? SolarProjectId { get; set; }
    public string? SelectedPlan { get; set; }
    public decimal PlanAmount { get; set; }
    public decimal RequestedAmount { get; set; }
    public decimal ApprovedAmount { get; set; }
    public ProjectStatus CurrentStage { get; set; }
    public ApprovalStatus ApprovalStatus { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? UserFullName { get; set; }
    public string? MemberFullName { get; set; }   // Live from m_membermaster (MemFirstName + MemLastName)
    public decimal TotalPaid { get; set; }
    public decimal TotalDue { get; set; }
    public List<DocumentDto> Documents { get; set; } = new();
}

public class CreateSolarRequestDto
{
    public string ApplicantName { get; set; } = string.Empty;
    public string MobileNumber { get; set; } = string.Empty;
    public string? AlternateMobile { get; set; }
    public string Email { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string PinCode { get; set; } = string.Empty;
    public string? AadharNumber { get; set; }
    public string? PANNumber { get; set; }
    public RequestType RequestType { get; set; } = RequestType.WithActivation;
    public ConnectionType ConnectionType { get; set; }
    public decimal KVCapacity { get; set; }
    public int? SolarProjectId { get; set; }
    public string? SelectedPlan { get; set; }
    public decimal PlanAmount { get; set; }
}

public class UpdateSolarRequestStatusDto
{
    public int Id { get; set; }
    public ProjectStatus NewStage { get; set; }
    public ApprovalStatus ApprovalStatus { get; set; }
    public string? Notes { get; set; }
    public string? RejectionReason { get; set; }
}