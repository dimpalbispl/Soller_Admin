using SolarPortal.Domain.Common;
using SolarPortal.Domain.Enums;

namespace SolarPortal.Domain.Entities;

public class Installation : BaseEntity
{
    public int SolarRequestId { get; set; }
    public DateTime? InstallationDate { get; set; }
    public string? Notes { get; set; }
    public string? Remark { get; set; }
    public int? AssignedWorkerId { get; set; }
    public bool IsCompleted { get; set; } = false;
    public DateTime? CompletedAt { get; set; }
    public string? CompletionPhotoPath { get; set; }

    // ─── Mark-Installed photo batch (change request point 11) ─────────────
    // The INC uploads up to 30 photos when marking an installation done; the
    // admin approves or rejects the WHOLE batch, so the decision lives here and
    // not on each InstallationPhoto row. A rejection sends the INC back to
    // re-upload, which resets this to Pending again.
    //
    // These columns are created by the INSTALLER panel's ADD-UserPanelIncPoints.sql
    // and are read by BOTH apps, so the names must match that panel exactly.

    /// <summary>
    /// Pending until admin reviews the photos, then Approved or Rejected.
    /// Approving does NOT pay here — see <see cref="CommissionCredited"/>.
    /// </summary>
    public ApprovalStatus ApprovalStatus { get; set; } = ApprovalStatus.Pending;

    /// <summary>Why admin sent it back — shown to the installer verbatim.</summary>
    public string? RejectionReason { get; set; }

    public DateTime? ReviewedAt { get; set; }
    public string? ReviewedBy { get; set; }

    /// <summary>Last time the installer submitted (or re-submitted) for review.</summary>
    public DateTime? SubmittedAt { get; set; }

    /// <summary>
    /// Set by the INSTALLER panel once it has actually posted the commission to
    /// the INC wallet. The admin app never writes it: the installer panel is the
    /// only place installation commission is paid, and it keys idempotency on
    /// IncCommissionLedger.SolarRequestId. Paying from here as well would bypass
    /// that check and credit the same project twice.
    /// </summary>
    public bool CommissionCredited { get; set; }

    public virtual Worker? AssignedWorker { get; set; }

    public virtual SolarRequest? SolarRequest { get; set; }
    public virtual ICollection<WorkerAssignment> WorkerAssignments { get; set; } = new List<WorkerAssignment>();
    public virtual ICollection<InstallationPhoto> Photos { get; set; } = new List<InstallationPhoto>();
}