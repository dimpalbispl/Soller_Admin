using SolarPortal.Domain.Common;

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

    public virtual Worker? AssignedWorker { get; set; }

    public virtual SolarRequest? SolarRequest { get; set; }
    public virtual ICollection<WorkerAssignment> WorkerAssignments { get; set; } = new List<WorkerAssignment>();
}