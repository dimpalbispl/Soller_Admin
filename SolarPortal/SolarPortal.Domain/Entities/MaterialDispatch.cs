using SolarPortal.Domain.Common;

namespace SolarPortal.Domain.Entities;

public class MaterialDispatch : BaseEntity
{
    public int SolarRequestId { get; set; }
    public string? MaterialDetails { get; set; }
    public DateTime? DispatchDate { get; set; }
    public string? DispatchDocumentPath { get; set; }
    public string? VehicleDetails { get; set; }
    public string? Remark { get; set; }
    public int? AssignedWorkerId { get; set; } // Despatch person / installer

    // ─── Two-step dispatch (change request point 6) ───────────────────────
    // "Prepare for Dispatch" and "Final Dispatch" are separate admin menus, so
    // a row now has two milestones instead of one. Prepare records the material,
    // vehicle and installer and leaves the project where it is; Final flips
    // IsDispatched and moves the project on to Installation.
    // A row sits in the Prepare queue while IsPrepared is false, and in the
    // Final queue while IsPrepared is true but IsDispatched is not.
    public bool IsPrepared { get; set; } = false;
    public DateTime? PreparedAt { get; set; }
    public string? PreparedBy { get; set; }
    public string? PrepareRemark { get; set; }

    public bool IsDispatched { get; set; } = false;
    public string? DispatchedBy { get; set; }

    public virtual Worker? AssignedWorker { get; set; }

    public virtual SolarRequest? SolarRequest { get; set; }
}