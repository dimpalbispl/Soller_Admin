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
    // Change request fields (auto-mapped from SolarRequest by AutoMapper)
    public string? LightBillOwnerType { get; set; }          // Task 3: 'Self' | 'BloodRelation'
    public string? LightBillRelationName { get; set; }       // Task 3/4
    public string? LightBillRelationProofPath { get; set; }  // Task 3
    public string? LightBillNumber { get; set; }             // Task 4
    public string? LightBillPath { get; set; }               // Task 4
    public string? PmSuryaLoanOption { get; set; }           // Task 7: 'Loan' | 'WithoutLoan'
    public string? PmSuryaApplicationNo { get; set; }        // Task 11
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

    // Legacy basic-product reference (V#SpProductDetail.ProdId) for With Activation flow.
    public int? ExternalProductId { get; set; }

    // Admin annotations on the request. RejectionReason is set when admin rejects;
    // AdminNotes is the general-purpose note (approval comment, follow-up, etc).
    // Both round-trip via AutoMapper from the matching SolarRequest entity fields.
    public string? RejectionReason { get; set; }
    public string? AdminNotes { get; set; }

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
    public string? LightBillOwnerType { get; set; }
    public string? LightBillRelationName { get; set; }
    public string? LightBillNumber { get; set; }
    public string? LightBillPath { get; set; }
    public string? LightBillRelationProofPath { get; set; }
    public string? PmSuryaLoanOption { get; set; }
    public int? ExternalProductId { get; set; }
}

public class UpdateSolarRequestStatusDto
{
    public int Id { get; set; }
    public ProjectStatus NewStage { get; set; }
    public ApprovalStatus ApprovalStatus { get; set; }
    public string? Notes { get; set; }
    public string? RejectionReason { get; set; }
}