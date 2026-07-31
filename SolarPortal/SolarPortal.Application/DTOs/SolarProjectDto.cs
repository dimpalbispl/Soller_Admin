using SolarPortal.Domain.Enums;

namespace SolarPortal.Application.DTOs;

public class SolarProjectDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public decimal SolarTypeKV { get; set; }
    public ConnectionType ConnectionType { get; set; }
    public int BV { get; set; }
    public int FinalBV { get; set; }
    public decimal DiscomWork { get; set; }
    public decimal DealClose { get; set; }
    public decimal SCZMenue { get; set; }
    public decimal SportainTeam { get; set; }
    public decimal TotalAmount { get; set; }
    public decimal ProjectAmount { get; set; }   // shown to user (e.g., 1 kW = ₹10,000)

    /// <summary>
    /// Flat rupee commission an INC worker earns on every project taken under
    /// this plan. NULL = not configured, so no payout can be generated for it.
    /// Set from the INC Commission page, shown read-only on the plan master.
    /// </summary>
    public decimal? IncCommissionAmount { get; set; }

    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class CreateSolarProjectDto
{
    public string Name { get; set; } = string.Empty;
    public decimal SolarTypeKV { get; set; }
    public ConnectionType ConnectionType { get; set; } = ConnectionType.Domestic;
    public int BV { get; set; } = 0;
    public int FinalBV { get; set; }
    public decimal DiscomWork { get; set; }
    public decimal DealClose { get; set; }
    public decimal SCZMenue { get; set; }
    public decimal SportainTeam { get; set; }
    public decimal TotalAmount { get; set; }
    public decimal ProjectAmount { get; set; }
    public bool IsActive { get; set; } = true;
}
