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
