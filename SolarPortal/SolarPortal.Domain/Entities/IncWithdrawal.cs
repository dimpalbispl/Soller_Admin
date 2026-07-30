namespace SolarPortal.Domain.Entities;

// INC worker withdrawal request. Maps to the existing IncWithdrawals table (no BaseEntity columns).
public class IncWithdrawal
{
    public int Id { get; set; }
    public int WorkerId { get; set; }
    public string? RequestNumber { get; set; }
    public decimal Amount { get; set; }

    // ── Payee bank details, captured per request ─────────────────────────
    // Snapshotted on the withdrawal rather than read from a profile: INC
    // workers are not M_MemberMaster members (Workers carries no Formno/Idno
    // and UserId is blank), so there is no member row to pull these from.
    // Storing them per request also keeps a record of where each payout was
    // actually sent, even if the worker later changes accounts.
    // BankName is chosen from the legacy M_BankMaster list.
    public string? BankName { get; set; }
    public string? IFSCode { get; set; }
    public string? BranchName { get; set; }
    public string? AccountNo { get; set; }

    /// <summary>
    /// Legacy free-text field. Kept so old rows keep their text; new requests
    /// write a readable one-line summary of the four fields above into it.
    /// </summary>
    public string? BankDetails { get; set; }

    public string Status { get; set; } = "Pending";   // Pending / Approved / Rejected
    public DateTime RequestedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ProcessedAt { get; set; }
    public string? ProcessedBy { get; set; }
    public string? AdminNotes { get; set; }
    public string? RejectionReason { get; set; }
}
