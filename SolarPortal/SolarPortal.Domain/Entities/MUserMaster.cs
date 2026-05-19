namespace SolarPortal.Domain.Entities;

/// <summary>
/// READ-ONLY mapping to existing m_usermaster table.
/// Source of truth for ADMIN credentials. Never altered by EF migrations.
/// </summary>
public class MUserMaster
{
    public decimal UId { get; set; }
    public decimal? GroupId { get; set; }
    public string? UserName { get; set; }
    public string? Passw { get; set; }
    public string? ActiveStatus { get; set; }
    public DateTime? CreateDate { get; set; }
    public string? Email { get; set; }
    public decimal? MobileNo { get; set; }
    public bool? IsCustomerCare { get; set; }
}
