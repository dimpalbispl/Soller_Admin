using SolarPortal.Domain.Common;
using SolarPortal.Domain.Enums;

namespace SolarPortal.Domain.Entities;

public class SolarProject : BaseEntity
{
    public string Name { get; set; } = string.Empty;       // e.g. "Plan A"
    public decimal SolarTypeKV { get; set; }               // 1.1, 3, 5, 10
    public ConnectionType ConnectionType { get; set; }     // Domestic / Commercial
    public int BV { get; set; } = 100;                     // default 100
    public int FinalBV { get; set; }                       // 110 / 175
    public decimal DiscomWork { get; set; }                // ₹1500
    public decimal DealClose { get; set; }                 // ₹1500
    public decimal SCZMenue { get; set; }                  // ₹3000
    public decimal SportainTeam { get; set; }              // ₹3000
    public decimal TotalAmount { get; set; }               // computed total / sale price (internal, not shown to user)
    public decimal ProjectAmount { get; set; }             // amount shown to user as "Project Amount" (e.g., 1 kW = ₹10,000)
    public bool IsActive { get; set; } = true;

    public virtual ICollection<SolarRequest> Requests { get; set; } = new List<SolarRequest>();
}
