using SolarPortal.Domain.Common;
using SolarPortal.Domain.Enums;

namespace SolarPortal.Domain.Entities;

// Solar connection registered by an INC/Installer worker (created in the user/installer panel).
public class IncConnection : BaseEntity
{
    public int WorkerId { get; set; }
    public string CustomerName { get; set; } = string.Empty;
    public string MobileNumber { get; set; } = string.Empty;
    public string? Address { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
    public ConnectionType ConnectionType { get; set; } = ConnectionType.Domestic;
    public decimal KVCapacity { get; set; }
    public string? DocumentPath { get; set; }
    public string Status { get; set; } = "Pending";   // Pending / Complete / Approved / Rejected
    public decimal CommissionAmount { get; set; }
    public string? AdminRemark { get; set; }
}