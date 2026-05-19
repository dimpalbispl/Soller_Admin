namespace SolarPortal.Application.DTOs;

public class WalletDto
{
    public int Id { get; set; }
    public string UserId { get; set; } = string.Empty;
    public decimal TotalIncome { get; set; }
    public decimal TDS { get; set; }
    public decimal NetAmount { get; set; }
    public decimal WithdrawnAmount { get; set; }
    public decimal PendingBalance { get; set; }
    public DateTime CreatedAt { get; set; }
}