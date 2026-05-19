using SolarPortal.Domain.Common;

namespace SolarPortal.Domain.Entities;

public class MeterDispatch : BaseEntity
{
    public int SolarRequestId { get; set; }
    public string MeterNumber { get; set; } = string.Empty;
    public string? MeterType { get; set; }
    public DateTime? DispatchDate { get; set; }
    public string? DispatchDocumentPath { get; set; }
    public string? CourierDetails { get; set; }
    public string? Remark { get; set; }
    public bool IsDispatched { get; set; } = false;
    public string? DispatchedBy { get; set; }

    public virtual SolarRequest? SolarRequest { get; set; }
}