using SolarPortal.Domain.Common;
using SolarPortal.Domain.Enums;

namespace SolarPortal.Domain.Entities;

/// <summary>
/// KYC record of a COMMISSION-earning (INC) installer — ONE row per worker.
///
/// Change request point 8: "INC — commission wale ka KYC upload system banana
/// hai, approve by admin. JOB wale ka KYC nahi lena hai." So a row here only ever
/// belongs to a Worker whose Type is <see cref="WorkerType.INC"/>; the admin
/// queue re-checks that against the Workers table rather than trusting a copy.
///
/// The shape mirrors the legacy member KYC page (KYC.aspx): three independently
/// verified sections — Address Proof, Bank Detail, PAN Card — each with its own
/// status and reject remark. The INSTALLER panel writes the data and the ADMIN
/// panel sets the three statuses; a section that comes back Rejected unlocks in
/// the installer panel so it can be corrected, exactly like the legacy page.
///
/// This class must stay property-for-property identical to the installer panel's
/// copy — both apps map the same table in the same database.
/// </summary>
public class IncKycDocument : BaseEntity
{
    public int WorkerId { get; set; }

    // ─── Section 1: Address Proof ────────────────────────────────────────────
    public string? Address { get; set; }
    public string? PinCode { get; set; }
    /// <summary>Legacy M_StateDivMaster.StateCode.</summary>
    public string? StateCode { get; set; }
    public string? StateName { get; set; }
    public string? District { get; set; }
    public string? City { get; set; }

    /// <summary>Legacy M_IdTypeMaster.Id — which paper is used as address proof.</summary>
    public int? IdProofTypeId { get; set; }
    public string? IdProofTypeName { get; set; }
    /// <summary>Number printed on that paper (Aadhaar no., voter id no., …).</summary>
    public string? IdProofNo { get; set; }

    public string? AddressProofFrontPath { get; set; }
    public string? AddressProofBackPath { get; set; }

    public ApprovalStatus AddressStatus { get; set; } = ApprovalStatus.Pending;
    /// <summary>Admin's reject remark for the address section.</summary>
    public string? AddressRemark { get; set; }
    public DateTime? AddressReviewedAt { get; set; }
    public string? AddressReviewedBy { get; set; }

    // ─── Section 2: Bank Detail ──────────────────────────────────────────────
    /// <summary>"SAVING ACCOUNT" / "CURRENT ACCOUNT" — same values as the legacy page.</summary>
    public string? AccountType { get; set; }
    public string? AccountNo { get; set; }
    /// <summary>Legacy M_BankMaster.BId.</summary>
    public int? BankId { get; set; }
    public string? BankName { get; set; }
    public string? BranchName { get; set; }
    public string? IfscCode { get; set; }
    public string? BankProofPath { get; set; }

    public ApprovalStatus BankStatus { get; set; } = ApprovalStatus.Pending;
    public string? BankRemark { get; set; }
    public DateTime? BankReviewedAt { get; set; }
    public string? BankReviewedBy { get; set; }

    // ─── Section 3: PAN Card ─────────────────────────────────────────────────
    public string? PanNo { get; set; }
    public string? PanProofPath { get; set; }

    public ApprovalStatus PanStatus { get; set; } = ApprovalStatus.Pending;
    public string? PanRemark { get; set; }
    public DateTime? PanReviewedAt { get; set; }
    public string? PanReviewedBy { get; set; }

    /// <summary>Last time the installer submitted or corrected anything.</summary>
    public DateTime? SubmittedAt { get; set; }

    public virtual Worker? Worker { get; set; }

    // ─── Derived helpers (not mapped) ────────────────────────────────────────

    /// <summary>True once admin has approved all three sections.</summary>
    public bool IsFullyApproved =>
        AddressStatus == ApprovalStatus.Approved &&
        BankStatus == ApprovalStatus.Approved &&
        PanStatus == ApprovalStatus.Approved;

    /// <summary>Any section still waiting on the admin — what the queue filters on.</summary>
    public bool HasPendingSection =>
        AddressStatus == ApprovalStatus.Pending ||
        BankStatus == ApprovalStatus.Pending ||
        PanStatus == ApprovalStatus.Pending;

    /// <summary>A section the admin sent back; the installer has to correct it.</summary>
    public bool HasRejectedSection =>
        AddressStatus == ApprovalStatus.Rejected ||
        BankStatus == ApprovalStatus.Rejected ||
        PanStatus == ApprovalStatus.Rejected;
}
