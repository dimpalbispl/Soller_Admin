namespace SolarPortal.Domain.Entities;

/// <summary>
/// READ-ONLY mapping to existing m_membermaster table in live solfitenergy DB.
/// Used as source of truth for USER credentials. Never altered by EF migrations.
/// </summary>
public class MMemberMaster
{
    public decimal MId { get; set; }
    public string? IdNo { get; set; }
    public string? Passw { get; set; }
    public string? MemFirstName { get; set; }
    public string? MemLastName { get; set; }
    public decimal? Mobl { get; set; }
    public string? EMail { get; set; }
    public string? PanNo { get; set; }
    public string? AadharNo { get; set; }
    public string? Address1 { get; set; }
    public string? City { get; set; }
    public string? District { get; set; }
    public decimal? StateCode { get; set; }
    public string? PinCode { get; set; }
    public decimal? BV { get; set; }
    public string? ActiveStatus { get; set; }

    public string FullName => $"{MemFirstName?.Trim()} {MemLastName?.Trim()}".Trim();
}
