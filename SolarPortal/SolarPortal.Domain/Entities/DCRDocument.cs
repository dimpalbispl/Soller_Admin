using SolarPortal.Domain.Common;
using SolarPortal.Domain.Enums;

namespace SolarPortal.Domain.Entities;

public class DCRDocument : BaseEntity
{
    public int SolarRequestId { get; set; }
    public string? DCRNumber { get; set; }
    public DateTime? DCRDate { get; set; }
    public string? DocumentPath { get; set; }
    // "DCR & Work Upload": admin uploads exactly two PDFs — DocumentPath holds the
    // DCR PDF, WorkDocumentPath the work-completion PDF. Column added by
    // ADD-DCRWorkDocument.sql.
    public string? WorkDocumentPath { get; set; }
    public string? Remark { get; set; }
    public string? ExtractedData { get; set; } // JSON from OCR
    public bool IsVerified { get; set; } = false;

    // ─── Admin approval workflow (per spec) ────────────────────────────
    // User uploads → ApprovalStatus = Pending
    // Admin clicks Approve → Approved, sets ApprovedAt + IsVerified=true,
    //                        SolarRequest stage advances to Completed
    // Admin clicks Reject → Rejected, RejectionReason captured,
    //                       user can re-upload from User panel
    public ApprovalStatus ApprovalStatus { get; set; } = ApprovalStatus.Pending;
    public string? RejectionReason { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public string? ApprovedBy { get; set; }

    public virtual SolarRequest? SolarRequest { get; set; }
}