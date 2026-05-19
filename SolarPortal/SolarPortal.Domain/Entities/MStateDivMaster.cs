namespace SolarPortal.Domain.Entities;

/// <summary>
/// READ-ONLY state master from live DB. Used for state dropdowns.
/// </summary>
public class MStateDivMaster
{
    public decimal StateCode { get; set; }
    public string? StateName { get; set; }
    public string? ActiveStatus { get; set; }
    public decimal? CountryCode { get; set; }
}
