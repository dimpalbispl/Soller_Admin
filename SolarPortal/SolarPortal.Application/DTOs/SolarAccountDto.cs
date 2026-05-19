using SolarPortal.Domain.Enums;

namespace SolarPortal.Application.DTOs;

public class SolarAccountDto
{
    public int Id { get; set; }
    public string AccountNumber { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public int SolarRequestId { get; set; }
    public decimal ProjectAmount { get; set; }
    public decimal DepositAmount { get; set; }
    public decimal DueAmount { get; set; }
    public ProjectStatus CurrentStatus { get; set; }
    public bool IsFrozen { get; set; }
    public DateTime CreatedAt { get; set; }
}