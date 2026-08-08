namespace SolarPortal.Application.Interfaces.Services;

/// <summary>
/// Credits an INC installer's commission for one project.
///
/// Change request point 11: "Admin se Approve hone par credit hona chahiye." —
/// so the money moves the moment the admin approves the mark-installed photos,
/// not later.
///
/// The INSTALLER panel has the same operation and also runs a catch-up sweep
/// when an installer opens their queue. Both paths therefore have to share ONE
/// idempotency key, and that key is <c>IncCommissionLedger.SolarRequestId</c> —
/// one project earns its commission exactly once, whichever app gets there
/// first. Anything keyed differently (a RefNo of our own, say) would be
/// invisible to the other side and the same project would be paid twice.
/// </summary>
public interface IIncCommissionCreditService
{
    Task<IncCommissionCreditResult> CreditForRequestAsync(int solarRequestId, int workerId, string performedBy);
}

public class IncCommissionCreditResult
{
    /// <summary>True only when this call is the one that actually posted the money.</summary>
    public bool Credited { get; set; }

    public decimal Amount { get; set; }

    /// <summary>Admin-facing explanation — including why nothing was credited.</summary>
    public string Message { get; set; } = string.Empty;
}
