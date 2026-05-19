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
    public bool IsDispatched { get; set; } = false;
    public string? DispatchedBy { get; set; }

    public virtual Worker? AssignedWorker { get; set; }

    public virtual SolarRequest? SolarRequest { get; set; }
}