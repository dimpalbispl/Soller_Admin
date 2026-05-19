using SolarPortal.Domain.Common;
using SolarPortal.Domain.Enums;
using System.Reflection.Metadata;

namespace SolarPortal.Domain.Entities;

public class SolarRequest : BaseEntity
{
    public string RequestNumber { get; set; } = string.Empty; // SCR-001
    public string UserId { get; set; } = string.Empty;

    // Applicant Info
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

    // Technical
    public RequestType RequestType { get; set; } = RequestType.WithActivation;
    public ConnectionType ConnectionType { get; set; }
    public decimal KVCapacity { get; set; }
    public int? SolarProjectId { get; set; }
    public string? SelectedPlan { get; set; }
    public decimal PlanAmount { get; set; }

    // Status
    public ProjectStatus CurrentStage { get; set; } = ProjectStatus.Registration;
    public ApprovalStatus ApprovalStatus { get; set; } = ApprovalStatus.Pending;
    public string? RejectionReason { get; set; }
    public string? AdminNotes { get; set; }

    // Navigation
    public virtual ApplicationUser? User { get; set; }
    public virtual SolarProject? SolarProject { get; set; }
    public virtual ICollection<Payment> Payments { get; set; } = new List<Payment>();
    public virtual ICollection<Document> Documents { get; set; } = new List<Document>();
    public virtual ICollection<SiteSurvey> SiteSurveys { get; set; } = new List<SiteSurvey>();
    public virtual ICollection<MeterDispatch> MeterDispatches { get; set; } = new List<MeterDispatch>();
    public virtual ICollection<MaterialDispatch> MaterialDispatches { get; set; } = new List<MaterialDispatch>();
    public virtual ICollection<Installation> Installations { get; set; } = new List<Installation>();
    public virtual ICollection<DCRDocument> DCRDocuments { get; set; } = new List<DCRDocument>();
    public virtual ICollection<Notification> Notifications { get; set; } = new List<Notification>();
    public virtual Commission? Commission { get; set; }
    public virtual SolarAccount? SolarAccount { get; set; }
}