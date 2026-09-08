using SolarPortal.Domain.Enums;

namespace SolarPortal.Application.Interfaces.Services;

/// <summary>
/// Read-only report over what Remaining BV approval posted into the legacy
/// Repurchincome ledger.
///
/// ONLY those rows. The same table also holds thousands of legacy activation
/// entries; this report never shows them, because the Remaining BV posts are
/// what an admin comes here to check. They are identified by the pair the
/// procedure hardcodes — BillType='T' and SoldBy='HO'.
///
/// A ledger row carries only a FormNo, so two lookups make it readable: the
/// member from M_MemberMaster, and the order side from RemainingBvUpdates.
/// </summary>
public interface IRepurchaseIncomeReportService
{
    /// <param name="from">Inclusive BillDate lower bound, or null.</param>
    /// <param name="to">Inclusive BillDate upper bound (whole day), or null.</param>
    /// <param name="top">Row cap.</param>
    Task<List<RepurchaseIncomeRow>> GetAsync(DateTime? from, DateTime? to, int top);
}

/// <summary>One Repurchincome row with the member and order details filled in.</summary>
public class RepurchaseIncomeRow
{
    // ─── the ledger row itself ────────────────────────────────────────────
    public long RId { get; set; }
    public string? BillNo { get; set; }
    public DateTime? BillDate { get; set; }

    /// <summary>Repurchincome.RecTimeStamp — the wall-clock moment it was written.</summary>
    public DateTime? PostedAt { get; set; }

    public decimal FormNo { get; set; }

    /// <summary>Repurchincome.Repurchincome — the BV this row credits.</summary>
    public decimal Bv { get; set; }

    public decimal Pv { get; set; }
    public string? BillType { get; set; }
    public string? SoldBy { get; set; }
    public string? Remarks { get; set; }

    public decimal SessId { get; set; }
    public int MSessId { get; set; }

    /// <summary>Day session — yyyymmdd. The Remaining BV row carries the same value.</summary>
    public decimal DSessId { get; set; }

    // ─── member, from M_MemberMaster ──────────────────────────────────────
    public string? MemberIdNo { get; set; }
    public string? MemberName { get; set; }
    public string? Mobile { get; set; }

    // ─── order side, from RemainingBvUpdates (null for legacy rows) ───────
    public string? RequestNumber { get; set; }
    public string? PlanName { get; set; }
    public string? OrderNo { get; set; }
    public decimal? SolarTypeKV { get; set; }
    public decimal? RemainingBv { get; set; }
    public string? SponsorIdNo { get; set; }

    /// <summary>The Remaining BV record's own status, null for a legacy row.</summary>
    public ApprovalStatus? BvStatus { get; set; }

    /// <summary>True when this row could be matched back to a Remaining BV record.</summary>
    public bool FromRemainingBv => RequestNumber != null;
}
