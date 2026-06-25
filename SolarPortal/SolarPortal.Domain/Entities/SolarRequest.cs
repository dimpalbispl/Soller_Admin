using SolarPortal.Domain.Common;
using SolarPortal.Domain.Enums;
using System.ComponentModel.DataAnnotations.Schema;
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

    // ─── Light Bill details (change request: Tasks 3 & 4) ────────────────
    // Whether the electricity bill is in the applicant's OWN name or a blood
    // relation's name. Values: "Self" | "BloodRelation". When "BloodRelation",
    // a relationship proof is mandatory (LightBillRelationProofPath).
    public string? LightBillOwnerType { get; set; }
    public string? LightBillRelationName { get; set; }   // whose name (if blood relation)
    public string? LightBillRelationProofPath { get; set; }
    public string? LightBillNumber { get; set; }         // consumer / bill number from KYC
    public string? LightBillPath { get; set; }           // the uploaded light-bill file

    // ─── PM Surya Ghar (change request: Tasks 7 & 11) ────────────────────
    public string? PmSuryaLoanOption { get; set; }       // "Loan" | "WithoutLoan"
    public string? PmSuryaApplicationNo { get; set; }    // entered by admin at approval

    // Technical
    public RequestType RequestType { get; set; } = RequestType.WithActivation;
    public ConnectionType ConnectionType { get; set; }
    public decimal KVCapacity { get; set; }
    public int? SolarProjectId { get; set; }
    public string? SelectedPlan { get; set; }
    public decimal PlanAmount { get; set; }

    // Legacy product reference. For "With Activation" mode, the user picks one of the
    // basic products from V#SpProductDetail (legacy cooperative DB view) — we store its
    // ProdId here. SolarProjectId is for our own SolarProjects master and is null in
    // this case. Only ONE of the two should be populated per request.
    public int? ExternalProductId { get; set; }

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

    // ─── Display-only field for admin views ──────────────────────────────
    // Populated by admin controllers via EnrichMemberNamesAsync (or left
    // null). Admin Razor views fall back to ApplicantName when this is
    // blank. Marked NotMapped so EF doesn't try to create a DB column.
    // Keeps the same shape as SolarRequestDto.MemberFullName so the views
    // can use either.
    [NotMapped]
    public string? MemberFullName { get; set; }
}