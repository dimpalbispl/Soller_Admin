namespace SolarPortal.Application.Services;

/// <summary>
/// Bridges new SolarPortal admin payment approvals into the legacy SolFit
/// MLM approval workflow. Ports the VB AprvAction(AprvType, ApprvStatus)
/// from the old admin "Pending Activation Request" page.
///
/// Triggered ONLY for solar requests where RequestType == WithActivation.
/// Per spec (paraphrased Hinglish):
///   "package name With Activation usk liye hi ye approve reject pr lgana h
///    other name jese without activation aready active aata h to ye call nhi krna h"
///
/// What it does on Approve (mirrors VB non-cash branch):
///   1. Idempotency check on TrnProductorderDetail.IsApprove
///   2. Lookup member info (Formno, ActiveStatus, KitId)
///   3. Lookup BV/PV totals from TrnProductorderDetail rows for this OrderNo
///   4. Lookup existing PV from Repurchincome (cumulative thresholds)
///   5. Lookup current sessid from M_SessnMaster
///   6. Resolve activation Kit (m_Kitmaster — BV-threshold based)
///   7. Insert TrnorderDetail rows (mirroring TrnProductorderDetail rows
///      joined with solfitenergyinv..M_ProductMaster for master data)
///   8. Insert TrnOrder header (from M_MemberMaster info)
///   9. Update TrnOrder with computed aggregates (OrderAmt, OrderItem, etc.)
///  10. Insert UserHistory audit row
///  11. Insert solfitenergyinv..TrnPaymentConfirmation row
///  12. Insert Repurchincome row (MLM commission ledger)
///  13. EXEC Sp_ActivateMember_New with kit level chosen by PV threshold
///       (>=4950 → kit 4, >=1975 → kit 5, else kit 4)
///  14. UPDATE TrnProductorderDetail SET IsApprove='Y'
///
/// All steps run inside a single SQL transaction. Any failure rolls back
/// the entire chain. The new app's payment record stays verified regardless
/// — admin can re-run the MLM approval manually if the bridge errors out
/// (failure is surfaced as a warning, not a hard fail).
///
/// Reject:
///   UPDATE TrnProductorderDetail SET IsApprove='R' (terminal — user must
///   submit a new request to retry).
/// </summary>
public interface ILegacyMlmApprovalService
{
    /// <summary>
    /// Run the full legacy MLM approval chain for a verified payment.
    /// Looks up the legacy OrderNo itself via UTR (Payment.UTRNumber maps to
    /// TrnProductorderDetail.txnid, possibly with a "REACT-" prefix from
    /// reactivation flow).
    /// </summary>
    Task<LegacyMlmResult> ApproveActivationAsync(LegacyMlmApprovalInput input);

    /// <summary>
    /// Marks the legacy TrnProductorderDetail row(s) as rejected.
    /// </summary>
    Task<LegacyMlmResult> RejectActivationAsync(LegacyMlmRejectInput input);
}

public class LegacyMlmApprovalInput
{
    /// <summary>Legacy IdNo of the member whose payment is being approved.</summary>
    public string MemberIdNo { get; set; } = string.Empty;
    /// <summary>UTR / Transaction number — used to locate the legacy OrderNo.</summary>
    public string Utr { get; set; } = string.Empty;
    /// <summary>The verified payment amount (used as KitMaster BV threshold).</summary>
    public decimal Amount { get; set; }
    /// <summary>Free-text remark shown in the approval audit (TrnProductorderDetail.ApproveRemark).</summary>
    public string ApproveRemark { get; set; } = "Admin verified payment";
    /// <summary>Admin's IdNo — used as SoldBy / PartyCode on Repurchincome.</summary>
    public string PartyCode { get; set; } = string.Empty;
    /// <summary>New app's request number for the audit trail.</summary>
    public string? RequestNumber { get; set; }
}

public class LegacyMlmRejectInput
{
    /// <summary>UTR to locate the legacy OrderNo.</summary>
    public string Utr { get; set; } = string.Empty;
    public string MemberIdNo { get; set; } = string.Empty;
    public string RejectRemark { get; set; } = string.Empty;
    public string? RequestNumber { get; set; }
}

public class LegacyMlmResult
{
    public bool Success { get; set; }
    public string? OrderNo { get; set; }
    public string? ErrorMessage { get; set; }
    /// <summary>True when the row was already approved/rejected (idempotent skip — not a failure).</summary>
    public bool AlreadyProcessed { get; set; }
}
