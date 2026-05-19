using SolarPortal.Domain.Common;

namespace SolarPortal.Domain.Entities;

public class WorkerAssignment : BaseEntity
{
    public int InstallationId { get; set; }
    public int WorkerId { get; set; }
    public string AssignedByUserId { get; set; } = string.Empty;
    public DateTime AssignedDate { get; set; } = DateTime.UtcNow;
    public string? Notes { get; set; }

    public virtual Installation? Installation { get; set; }
    public virtual Worker? Worker { get; set; }
}