namespace SolarPortal.Application.Services;

/// <summary>
/// Reads payment modes from the legacy SolFit table M_PayModeMaster so the
/// new portal's payment forms show exactly the same options as the old
/// system (per spec: "payment mode M_PayModeMaster table se show karo sabhi
/// jagah").
///
/// The legacy table is keyed by Pid and carries the display text in Paymode,
/// plus flags (IsTransNo, IsBankDtl, IsBranchDtl) the old UI used to decide
/// which extra fields to show. We surface Pid + Paymode + IsTransNo; the rest
/// can be added later if needed.
/// </summary>
public interface IPayModeService
{
    Task<List<PayModeDto>> GetActiveAsync();
}

public class PayModeDto
{
    /// <summary>Legacy M_PayModeMaster.PId — the stable numeric id.</summary>
    public int Pid { get; set; }
    /// <summary>Display text, e.g. "UPI", "Bank Transfer".</summary>
    public string Paymode { get; set; } = string.Empty;
    /// <summary>Whether a transaction/UTR number is expected for this mode.</summary>
    public bool RequiresTransNo { get; set; }
    /// <summary>Custom label for the transaction-number field, e.g. "UTR No.", "Cheque No." (from TransNoLbl).</summary>
    public string TransNoLabel { get; set; } = string.Empty;
}
