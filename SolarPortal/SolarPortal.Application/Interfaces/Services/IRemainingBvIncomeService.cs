namespace SolarPortal.Application.Interfaces.Services;

/// <summary>
/// Posts an approved "Update Remaining BV" record into the legacy MLM ledger by
/// running dbo.Sp_UpdateReaminingBV (see SolarPortal/SP-UpdateRemainingBV.sql).
///
/// The procedure does two things in one transaction:
///   * inserts the member's remaining BV into Repurchincome — the row every MLM
///     income calculation reads;
///   * stamps RemainingBvUpdates.SessID with today's date in 112 (yyyymmdd)
///     form, the same value it writes to Repurchincome.DSessid, so both sides
///     agree on which day the BV was posted.
///
/// It runs BEFORE the row flips to Approved. A failure here therefore leaves the
/// record Pending and re-approvable, rather than frozen with no ledger entry —
/// approval is what makes the money payable, so it must not outrun the ledger.
/// Once it has run, the caller stamps PayoutPostedAt, and EnsureEditable refuses
/// the row from then on. That stamp is the ONLY thing preventing a second BV
/// credit for the same record.
/// </summary>
public interface IRemainingBvIncomeService
{
    /// <param name="rbvId">RemainingBvUpdates.Id — the row whose SessID gets stamped.</param>
    /// <param name="memberIdNo">Legacy M_MemberMaster.IDNo the BV is credited to.</param>
    /// <param name="remainingBv">TotalBV - FixedBV, the figure this screen distributes.</param>
    /// <param name="productName">Goes into Repurchincome.Remarks.</param>
    Task<RemainingBvPostResult> PostAsync(int rbvId, string memberIdNo, decimal remainingBv, string? productName);
}

public class RemainingBvPostResult
{
    /// <summary>True only when the procedure reported SUCCESS.</summary>
    public bool Posted { get; set; }

    /// <summary>The bill number that was allocated for the Repurchincome row.</summary>
    public string? BillNo { get; set; }

    /// <summary>Admin-facing explanation — including why nothing was posted.</summary>
    public string Message { get; set; } = string.Empty;
}
