using SolarPortal.Domain.Common;
using SolarPortal.Domain.Enums;
using System.ComponentModel.DataAnnotations.Schema;

namespace SolarPortal.Domain.Entities;

/// <summary>
/// "Update Remaining BV" - one row per SolarRequest.
///
/// Spec:
///   * Sponsor ID    - filled in automatically from the member's sponsor.
///   * Discom Income - Self / Other. "Self" stores the member's own ID; "Other"
///                     takes a typed ID that must sit ABOVE the member in the
///                     sponsor tree, never below.
///   * Deal Close    - same rule.
///   * SCI Income    - always the sponsor's ID.
///   * Saved -> shows in the admin panel. Admin may correct it, then either give
///     FINAL APPROVAL or REJECT it. A rejected record goes back to the member,
///     who fixes it and saves again onto the SAME row - never a duplicate.
///     After final approval nothing can be changed, and that day the income is
///     added to the daily payout.
///
/// Both the BV figures and the per-head money amounts are a SNAPSHOT taken when
/// the member submitted. The SolarProjects master can be re-priced later, and an
/// approved payout has to stay reproducible for the day it was approved.
///
/// Table created out-of-band by ADD-RemainingBv.sql (same workflow as
/// InstallationPhotos / IncKycDocuments - no EF migration is generated).
/// </summary>
public class RemainingBvUpdate : BaseEntity
{
    public int SolarRequestId { get; set; }
    public string? RequestNumber { get; set; }

    // ─── Member (from the legacy m_membermaster) ──────────────────────────
    public string MemberIdNo { get; set; } = string.Empty;
    public string? MemberName { get; set; }
    public decimal MemberFormNo { get; set; }

    // ─── Sponsor (auto, never typed by the member) ────────────────────────
    public string? SponsorIdNo { get; set; }
    public string? SponsorName { get; set; }

    // ─── The plan taken, from the SolarProjects master ────────────────────
    // This is what the member recognises - "which KV did I take" - so it is what
    // the screens show. Snapshotted for the same reason the money is: the master
    // can be renamed or re-priced later.
    public string? PlanName { get; set; }
    public decimal SolarTypeKV { get; set; }

    // ─── The verified product order this BV belongs to ────────────────────
    // Kept for audit / reconciliation against the legacy order, not shown as the
    // headline on the member's screens.
    public string? OrderNo { get; set; }
    public string? ProductName { get; set; }

    // ─── BV snapshot ──────────────────────────────────────────────────────
    /// <summary>Full BV of the plan taken - SolarProjects.FinalBV.</summary>
    public decimal TotalBV { get; set; }

    /// <summary>What the solar request itself already consumed (the product's 100 BV).</summary>
    public decimal FixedBV { get; set; }

    /// <summary>TotalBV - FixedBV. What this screen distributes.</summary>
    public decimal RemainingBV { get; set; }

    /// <summary>Human-readable trace of where TotalBV and FixedBV came from.</summary>
    public string? BvSource { get; set; }

    // ─── Income heads ─────────────────────────────────────────────────────
    // The Amount on each head is the money that head pays, copied from the
    // SolarProjects master (DiscomWork / DealClose / SCZMenue). It is display
    // only - neither the member nor the admin types it.

    public BvBeneficiaryMode DiscomIncomeMode { get; set; } = BvBeneficiaryMode.Self;
    public string? DiscomIncomeIdNo { get; set; }
    public string? DiscomIncomeName { get; set; }
    public decimal DiscomIncomeAmount { get; set; }

    public BvBeneficiaryMode DealCloseMode { get; set; } = BvBeneficiaryMode.Self;
    public string? DealCloseIdNo { get; set; }
    public string? DealCloseName { get; set; }
    public decimal DealCloseAmount { get; set; }

    /// <summary>Always the sponsor's IdNo, kept as its own column so a later
    /// sponsor change never rewrites a record that was already paid.</summary>
    public string? SciIncomeIdNo { get; set; }
    public string? SciIncomeName { get; set; }
    public decimal SciIncomeAmount { get; set; }

    // ─── Admin verification ───────────────────────────────────────────────
    /// <summary>Pending -> Approved (frozen) or Rejected (back to the member).</summary>
    public ApprovalStatus Status { get; set; } = ApprovalStatus.Pending;

    /// <summary>When the member submitted (or re-submitted after a rejection).</summary>
    public DateTime? SubmittedAt { get; set; }

    /// <summary>Admin's free-text note, shown to the member as-is.</summary>
    public string? AdminRemark { get; set; }

    /// <summary>Why the admin sent it back. Shown to the member verbatim.</summary>
    public string? RejectionReason { get; set; }

    public DateTime? CorrectedAt { get; set; }
    public string? CorrectedBy { get; set; }

    public DateTime? RejectedAt { get; set; }
    public string? RejectedBy { get; set; }

    /// <summary>The day the income becomes payable. Once set the row is frozen.</summary>
    public DateTime? ApprovedAt { get; set; }
    public string? ApprovedBy { get; set; }

    /// <summary>
    /// Stamped by whoever posts the approved income into the daily payout, so a
    /// repeated payout run can never pay the same row twice.
    /// </summary>
    public DateTime? PayoutPostedAt { get; set; }

    public virtual SolarRequest? SolarRequest { get; set; }

    /// <summary>Approved rows are read-only for EVERY actor, member and admin alike.</summary>
    [NotMapped]
    public bool IsLocked => Status == ApprovalStatus.Approved;

    /// <summary>
    /// A rejected record is editable again by the member. It reuses this same row,
    /// which is what keeps a re-submission from creating a duplicate.
    /// </summary>
    [NotMapped]
    public bool IsRejected => Status == ApprovalStatus.Rejected;

    /// <summary>Total money this record pays out across the three heads.</summary>
    [NotMapped]
    public decimal TotalIncome => DiscomIncomeAmount + DealCloseAmount + SciIncomeAmount;
}
