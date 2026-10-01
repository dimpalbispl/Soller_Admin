using SolarPortal.Domain.Common;
using SolarPortal.Domain.Enums;

namespace SolarPortal.Domain.Entities;

/// <summary>
/// A refund of money a member paid over and above what their project needed.
///
/// Two steps on purpose, like every other money movement in this panel: one admin
/// RAISES the refund (member IdNo, amount, which wallet it should land in), and a
/// decision — approve or reject — is recorded against it. Only an APPROVED refund
/// is written to the legacy ledger (IncTrnvoucher), credited to the member's
/// wallet and tagged with the project it belongs to.
///
/// Nothing is posted while the row is Pending, so a raised refund can be rejected
/// without any money having moved.
/// </summary>
public class ExtraPaymentRefund : BaseEntity
{
    /// <summary>The project the extra money was paid against.</summary>
    public int SolarRequestId { get; set; }

    /// <summary>Denormalised so the list reads without a join.</summary>
    public string RequestNumber { get; set; } = string.Empty;

    /// <summary>Member the money goes back to (the IdNo the admin typed).</summary>
    public string MemberIdNo { get; set; } = string.Empty;

    public string? MemberName { get; set; }

    public decimal Amount { get; set; }

    /// <summary>
    /// Which way the money moves, in the legacy ledger's own codes:
    ///   "C" — CREDIT: paid back into the member's wallet (DrTo '0', CrTo member).
    ///   "D" — DEBIT:  taken back out of it   (DrTo member, CrTo '0'), the same
    ///         shape the legacy "Amount deducted…" rows use.
    /// Approval posts whichever of the two this says; nothing moves before that.
    /// </summary>
    public string EntryType { get; set; } = "C";

    /// <summary>True when this entry pays the member (the default).</summary>
    public bool IsCredit => !string.Equals(EntryType, "D", StringComparison.OrdinalIgnoreCase);

    /// <summary>IncVouchertype.Acid — which wallet the credit is posted to.</summary>
    public int VoucherTypeId { get; set; }

    /// <summary>IncVouchertype.Actype ('I' = INC Wallet) — copied onto the ledger row.</summary>
    public string VoucherAcType { get; set; } = "I";

    /// <summary>IncVouchertype.WalletName, kept for display after the fact.</summary>
    public string? VoucherTypeName { get; set; }

    public string? Remark { get; set; }

    public ApprovalStatus Status { get; set; } = ApprovalStatus.Pending;

    public string? RequestedBy { get; set; }
    public DateTime? RequestedAt { get; set; }

    public string? DecidedBy { get; set; }
    public DateTime? DecidedAt { get; set; }
    public string? RejectionReason { get; set; }

    /// <summary>RefNo written on the IncTrnvoucher row, so the two can be tied together.</summary>
    public string? VoucherRefNo { get; set; }

    /// <summary>Set once the ledger row exists — the guard against a double credit.</summary>
    public DateTime? PostedAt { get; set; }

    public virtual SolarRequest? SolarRequest { get; set; }
}
