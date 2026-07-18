using SolarPortal.Domain.Common;
using SolarPortal.Domain.Enums;

namespace SolarPortal.Domain.Entities;

public class Worker : BaseEntity
{
    public string Name { get; set; } = string.Empty;
    public string MobileNumber { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string Specialization { get; set; } = string.Empty; // Electrician, Plumber, etc.
    public WorkerType Type { get; set; } = WorkerType.JOB;
    public bool IsAvailable { get; set; } = true;
    public string? City { get; set; }
    public string? State { get; set; }

    // INC worker login + commission (Phase 1). Only for INC-type workers.
    public string? LoginUsername { get; set; }
    public string? LoginPassword { get; set; }
    public decimal? CommissionPercent { get; set; }

    public virtual ICollection<WorkerAssignment> Assignments { get; set; } = new List<WorkerAssignment>();
}