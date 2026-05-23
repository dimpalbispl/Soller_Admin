namespace SolarPortal.Application.Services;

/// <summary>
/// Reads states from the legacy SolFit table M_StateDivMaster so the portal's
/// State dropdowns match the old system (per spec: "state bhi is table se hi
/// show karna hai"). Mirrors the legacy VB Fill_State():
///     SELECT StateCode, StateName FROM M_StateDivMaster
///     WHERE ActiveStatus='Y' AND RowStatus='Y' ORDER BY StateName
/// </summary>
public interface IStateService
{
    Task<List<StateDto>> GetActiveAsync();
}

public class StateDto
{
    /// <summary>Legacy M_StateDivMaster.StateCode — stable id.</summary>
    public string StateCode { get; set; } = string.Empty;
    /// <summary>Display name, e.g. "Rajasthan".</summary>
    public string StateName { get; set; } = string.Empty;
}
