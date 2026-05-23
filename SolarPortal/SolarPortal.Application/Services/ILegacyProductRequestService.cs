namespace SolarPortal.Application.Services;

/// <summary>
/// Bridges new SolarPortal "With Activation" submissions into the
/// legacy SolFit ASP.NET (VB) table TrnProductorderDetail.
///
/// Per spec ("Request.QueryString tp=A wala hi work karna h ism"):
///   Jab user new SolarPortal mein "With Activation" mode select karke
///   product pick aur submit kare, woh row legacy table mein bhi insert
///   hona chahiye taaki existing legacy reports / activation workflow
///   unko process kar sake. Yeh dual-write hai — new app ka apna
///   SolarRequest record alag rehta hai (workflow, status, payments).
///
/// All fields mirror the original VB Insert in ProductWalletRequest.aspx.vb
/// (RequestPin / "tp=A" branch). Where the legacy code reads from the
/// admin form (transaction no, dispatch date, address, image), we pass
/// values that the user already entered in the new payment + profile
/// flow. Image (ImageUpload column) is copied to wwwroot/Images/UploadImage
/// so the legacy admin viewer can still display it.
/// </summary>
public interface ILegacyProductRequestService
{
    /// <summary>
    /// Creates a TrnProductorderDetail row for the picked product.
    /// Returns the assigned OrderNo so callers can store / log it.
    /// </summary>
    Task<LegacyInsertResult> InsertWithActivationAsync(LegacyProductRequestInput input);
}

public class LegacyProductRequestInput
{
    /// <summary>Legacy m_membermaster.Formno — bridge resolves from IdNo.</summary>
    public string MemberIdNo { get; set; } = string.Empty;
    /// <summary>Selected product ID from V#SpProductDetail (BasicProductDto.ProdId).</summary>
    public int ProductId { get; set; }
    /// <summary>Quantity ordered. Defaults to 1 in the With Activation flow.</summary>
    public int Qty { get; set; } = 1;
    /// <summary>Payment UTR / Transaction number from new payment form.</summary>
    public string? TxnId { get; set; }
    /// <summary>Payment date.</summary>
    public DateTime? TxnDate { get; set; }
    /// <summary>Payment mode (PID into M_PayModeMaster). Defaults to UPI=1 if unknown.</summary>
    public int PayModeId { get; set; } = 1;
    /// <summary>Optional uploaded image filename — caller saves the bytes first.</summary>
    public string? ImageFileName { get; set; }
    /// <summary>Delivery / billing address from profile.</summary>
    public string? Address { get; set; }
    public string? City { get; set; }
    public string? District { get; set; }
    public string? PinCode { get; set; }
    public string? StateName { get; set; }
    public string? StateCode { get; set; }
}

public class LegacyInsertResult
{
    public bool Success { get; set; }
    public string? OrderNo { get; set; }
    public string? ErrorMessage { get; set; }
}
