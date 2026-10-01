using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SolarPortal.Application.DTOs;
using SolarPortal.Application.Interfaces.Services;
using SolarPortal.Application.Services;
using SolarPortal.Domain.Entities;
using SolarPortal.Domain.Enums;
using SolarPortal.Infrastructure.Data;

namespace SolarPortal.AdminWeb.Areas.SolarPanelAdmin.Controllers;

/// <summary>
/// Admin verification of "Update Remaining BV" (ADD-RemainingBv.sql, step 3).
///
/// The member fills the record in the user panel and saves it as Pending. From
/// here the admin may CORRECT the two editable heads, then either give FINAL
/// APPROVAL — which freezes the row and makes the income payable that day — or
/// REJECT it with a reason, which sends the same row back to the member to fix
/// and re-submit. There is never a second row for the same request.
///
/// What the admin may change and what they may not:
///   * Discom Income / Deal Close — Self or Other, and an "Other" ID is checked
///     against the SAME upline rule the member's own save is checked against.
///   * SCI Income — never editable, it is always the sponsor by definition.
///   * The money on each head and every BV figure — never editable. They are a
///     snapshot the member's submit took from the SolarProjects master, and an
///     approved payout has to stay reproducible for the day it was approved.
///
/// APPROVAL ALSO POSTS THE BV. Final approval runs dbo.Sp_UpdateReaminingBV
/// (SolarPortal/SP-UpdateRemainingBV.sql) through IRemainingBvIncomeService,
/// which inserts the remaining BV into the legacy Repurchincome ledger and
/// stamps RemainingBvUpdates.SessID with today's date in 112 (yyyymmdd) form.
/// The post runs BEFORE the row flips to Approved, so a ledger failure leaves
/// the record Pending and re-approvable instead of frozen and unpaid.
///
/// APPROVE IS ONE-SHOT. Both approve paths claim the row with a single
/// conditional UPDATE before the ledger runs (<see cref="ClaimForPostAsync"/>),
/// so a double-clicked button — or a resubmitted POST — cannot credit the same
/// BV twice. The click that loses is told the record is already approved.
///
/// While "RemainingBv:StayPendingAfterPost" is true the row is left Pending even
/// on success, so the posted data can be checked and posted again — see
/// <see cref="StayPendingAfterPost"/> for what that turns off.
/// </summary>
[Area("SolarPanelAdmin")]
[Authorize(Roles = "Admin")]
public class RemainingBvController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly ISponsorTreeService _sponsorTree;
    private readonly INotificationService _notifications;
    private readonly IAdminActivityLogger _activity;
    private readonly IRemainingBvIncomeService _bvIncome;
    private readonly IConfiguration _config;
    private readonly UserManager<ApplicationUser> _userManager;

    public RemainingBvController(
        ApplicationDbContext db,
        ISponsorTreeService sponsorTree,
        INotificationService notifications,
        IAdminActivityLogger activity,
        IRemainingBvIncomeService bvIncome,
        IConfiguration config,
        UserManager<ApplicationUser> userManager)
    {
        _db = db;
        _sponsorTree = sponsorTree;
        _notifications = notifications;
        _activity = activity;
        _bvIncome = bvIncome;
        _config = config;
        _userManager = userManager;
    }

    private string AdminId => _userManager.GetUserId(User) ?? "system";
    private string? ClientIp => HttpContext.Connection.RemoteIpAddress?.ToString();

    /// <summary>
    /// VERIFICATION MODE — appsettings "RemainingBv:StayPendingAfterPost".
    ///
    /// On: approve runs Sp_UpdateReaminingBV as normal, but the record is left
    /// PENDING instead of being frozen, so the Repurchincome row and the stamped
    /// SessID can be checked and the same record posted again.
    ///
    /// That means the double-credit guard is off for DELIBERATE repeats: the
    /// claim is released again after a successful post, so every fresh approve
    /// click writes ANOTHER Repurchincome row. Only a double-click is still
    /// caught, because the claim is held for the length of the post. Leave this
    /// false on live — it is what makes a second approval say "already approved"
    /// instead of paying again.
    /// </summary>
    private bool StayPendingAfterPost => _config.GetValue<bool>("RemainingBv:StayPendingAfterPost");

    // GET: /SolarPanelAdmin/RemainingBv?status=all
    //
    // The tab defaults to ALL, not Pending: a decided record is the one the admin
    // usually comes back to look up, and landing on an empty Pending queue hid it.
    public async Task<IActionResult> Index(string? status)
    {
        var f = (status ?? "all").Trim().ToLowerInvariant();

        var q = _db.RemainingBvUpdates.AsNoTracking();
        q = f switch
        {
            "approved" => q.Where(x => x.Status == ApprovalStatus.Approved),
            "rejected" => q.Where(x => x.Status == ApprovalStatus.Rejected),
            "pending" => q.Where(x => x.Status == ApprovalStatus.Pending),
            _ => q
        };

        var rows = await q
            .OrderByDescending(x => x.SubmittedAt ?? x.CreatedAt)
            .ToListAsync();

        ViewBag.Status = f;
        // Counts are read off the unfiltered set so the tabs show a real queue
        // size, not the size of whatever tab happens to be open.
        ViewBag.PendingCount = await _db.RemainingBvUpdates.CountAsync(x => x.Status == ApprovalStatus.Pending);
        ViewBag.ApprovedCount = await _db.RemainingBvUpdates.CountAsync(x => x.Status == ApprovalStatus.Approved);
        ViewBag.RejectedCount = await _db.RemainingBvUpdates.CountAsync(x => x.Status == ApprovalStatus.Rejected);
        return View(rows);
    }

    /// <summary>
    /// Saves the admin's correction, optionally approving in the same step
    /// (<paramref name="decision"/> = "approve"). Correcting is only possible
    /// while the row is Pending: an approved row is frozen, and a rejected one
    /// is back with the member.
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Review(
        int id,
        string? decision,
        BvBeneficiaryMode DiscomIncomeMode,
        string? DiscomIncomeIdNo,
        BvBeneficiaryMode DealCloseMode,
        string? DealCloseIdNo,
        string? remark,
        string? status)
    {
        var row = await _db.RemainingBvUpdates.FirstOrDefaultAsync(x => x.Id == id);
        if (row == null)
        {
            TempData["Error"] = "Remaining BV record not found.";
            return Back(status);
        }

        if (!EnsureEditable(row, out var why))
        {
            TempData["Error"] = why;
            return Back(status);
        }

        var discom = await ResolveHeadAsync(row, DiscomIncomeMode, DiscomIncomeIdNo, "Discom Income");
        var deal = await ResolveHeadAsync(row, DealCloseMode, DealCloseIdNo, "Deal Close");

        if (discom.Error != null || deal.Error != null)
        {
            TempData["Error"] = string.Join(" ", new[] { discom.Error, deal.Error }.Where(m => m != null));
            return Back(status);
        }

        var changed =
            row.DiscomIncomeMode != DiscomIncomeMode ||
            !string.Equals(row.DiscomIncomeIdNo, discom.IdNo, StringComparison.OrdinalIgnoreCase) ||
            row.DealCloseMode != DealCloseMode ||
            !string.Equals(row.DealCloseIdNo, deal.IdNo, StringComparison.OrdinalIgnoreCase);

        var now = DateTime.UtcNow;

        row.DiscomIncomeMode = DiscomIncomeMode;
        row.DiscomIncomeIdNo = discom.IdNo;
        row.DiscomIncomeName = discom.Name;
        row.DealCloseMode = DealCloseMode;
        row.DealCloseIdNo = deal.IdNo;
        row.DealCloseName = deal.Name;

        if (!string.IsNullOrWhiteSpace(remark)) row.AdminRemark = remark.Trim();

        if (changed)
        {
            row.CorrectedAt = now;
            row.CorrectedBy = AdminId;
        }

        row.UpdatedAt = now;
        row.UpdatedBy = AdminId;

        if (string.Equals(decision, "approve", StringComparison.OrdinalIgnoreCase))
        {
            if (!EnsurePayable(row, out var blocked))
            {
                TempData["Error"] = blocked;
                return Back(status);
            }

            // The correction is saved on its own first. If the ledger post below
            // fails the row stays Pending, and the admin should not have to type
            // their correction a second time to retry the approval.
            await _db.SaveChangesAsync();
            await SyncHeadFormNosAsync(row);

            // Claim the row BEFORE the ledger runs — see ClaimForPostAsync. The
            // second half of a double-click lands here and stops.
            if (!await ClaimForPostAsync(row, now))
            {
                TempData["Error"] = AlreadyApprovedMessage;
                return Back(status);
            }

            var posted = await PostToLedgerAsync(row);
            if (!posted.Posted)
            {
                await ReleaseClaimAsync(row);
                TempData["Error"] = $"Not approved — {posted.Message}";
                return Back(status);
            }

            // Verification mode: the correction is already saved above, so there
            // is nothing more to write — just release the claim, log it and leave
            // the row Pending.
            if (StayPendingAfterPost)
            {
                await ReleaseClaimAsync(row);
                await LeftPendingAsync(row, posted, corrected: changed);
                TempData["Success"] = "Approved successfully.";
                return Back(status);
            }

            ApplyApproval(row, now);
            await _db.SaveChangesAsync();
            await AfterApprovedAsync(row, corrected: changed, posted: posted);

            TempData["Success"] = "Approved successfully.";
            return Back(status);
        }

        await _db.SaveChangesAsync();
        await SyncHeadFormNosAsync(row);

        if (changed)
        {
            await _activity.LogAsync(AdminId, "RemainingBv.Correct", "RemainingBvUpdate", row.Id.ToString(),
                $"Corrected Remaining BV for {row.MemberIdNo} ({row.RequestNumber}). " +
                $"Discom Income -> {row.DiscomIncomeMode} {row.DiscomIncomeIdNo}; " +
                $"Deal Close -> {row.DealCloseMode} {row.DealCloseIdNo}.", ClientIp);
            TempData["Success"] = "Correction saved.";
        }
        else
        {
            TempData["Success"] = "Nothing changed.";
        }

        return Back(status);
    }

    // POST: final approval with no correction.
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Approve(int id, string? remark, string? status)
    {
        var row = await _db.RemainingBvUpdates.FirstOrDefaultAsync(x => x.Id == id);
        if (row == null)
        {
            TempData["Error"] = "Remaining BV record not found.";
            return Back(status);
        }

        if (!EnsureEditable(row, out var why))
        {
            TempData["Error"] = why;
            return Back(status);
        }

        if (!EnsurePayable(row, out var blocked))
        {
            TempData["Error"] = blocked;
            return Back(status);
        }

        var now = DateTime.UtcNow;

        // Claim the row BEFORE the ledger runs — see ClaimForPostAsync. The
        // second half of a double-click lands here and stops.
        if (!await ClaimForPostAsync(row, now))
        {
            TempData["Error"] = AlreadyApprovedMessage;
            return Back(status);
        }

        // Ledger first, approval second — see PostToLedgerAsync.
        var posted = await PostToLedgerAsync(row);
        if (!posted.Posted)
        {
            await ReleaseClaimAsync(row);
            TempData["Error"] = $"Not approved — {posted.Message}";
            return Back(status);
        }

        if (!string.IsNullOrWhiteSpace(remark)) row.AdminRemark = remark.Trim();

        if (StayPendingAfterPost)
        {
            await ReleaseClaimAsync(row);
            row.UpdatedAt = now;
            row.UpdatedBy = AdminId;
            await _db.SaveChangesAsync();
            await LeftPendingAsync(row, posted, corrected: false);
            TempData["Success"] = "Approved successfully.";
            return Back(status);
        }

        ApplyApproval(row, now);

        await _db.SaveChangesAsync();
        await AfterApprovedAsync(row, corrected: false, posted: posted);

        TempData["Success"] = "Approved successfully.";
        return Back(status);
    }

    // POST: send it back to the member. A reason is mandatory — it is the only
    // thing the member sees when deciding what to change.
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Reject(int id, string? reason, string? status)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            TempData["Error"] = "A rejection reason is required so the member knows what to correct.";
            return Back(status);
        }

        var row = await _db.RemainingBvUpdates.FirstOrDefaultAsync(x => x.Id == id);
        if (row == null)
        {
            TempData["Error"] = "Remaining BV record not found.";
            return Back(status);
        }

        if (!EnsureEditable(row, out var why))
        {
            TempData["Error"] = why;
            return Back(status);
        }

        var now = DateTime.UtcNow;
        row.Status = ApprovalStatus.Rejected;
        row.RejectionReason = reason.Trim();
        row.RejectedAt = now;
        row.RejectedBy = AdminId;
        row.UpdatedAt = now;
        row.UpdatedBy = AdminId;

        await _db.SaveChangesAsync();

        await _activity.LogAsync(AdminId, "RemainingBv.Reject", "RemainingBvUpdate", row.Id.ToString(),
            $"Rejected Remaining BV for {row.MemberIdNo} ({row.RequestNumber}). Reason: {row.RejectionReason}", ClientIp);

        await NotifyMemberAsync(row, "Remaining BV sent back",
            $"Your Remaining BV record was sent back for correction. Reason: {row.RejectionReason}");

        TempData["Success"] = "Rejected successfully.";
        return Back(status);
    }

    /// <summary>
    /// AJAX for the correction form's "Other" box — same check the member's own
    /// screen runs, so the admin sees the verdict before saving rather than as a
    /// failed POST.
    /// </summary>
    // GET: /SolarPanelAdmin/RemainingBv/VerifyId?id=5&idNo=SOLFIT1
    [HttpGet]
    public async Task<IActionResult> VerifyId(int id, string? idNo)
    {
        var row = await _db.RemainingBvUpdates.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (row == null) return Json(new { ok = false, message = "Record not found." });

        var check = await _sponsorTree.CheckRelationAsync(row.MemberIdNo, idNo ?? string.Empty);
        return Json(new
        {
            ok = check.IsAcceptable,
            idNo = check.IdNo,
            name = check.Name,
            level = check.Level,
            relation = check.Relation.ToString(),
            message = check.Message
        });
    }

    // ─── helpers ──────────────────────────────────────────────────────────

    private IActionResult Back(string? status) =>
        RedirectToAction(nameof(Index), new { status });

    /// <summary>
    /// Only a Pending row is the admin's to act on. Approved is final for every
    /// actor, and a Rejected row is sitting with the member — deciding it from
    /// here would overwrite whatever they are in the middle of fixing.
    /// </summary>
    private static bool EnsureEditable(RemainingBvUpdate row, out string? why)
    {
        if (row.PayoutPostedAt != null)
        {
            why = "This record is ALREADY APPROVED — its BV has already been posted to the payout, so it cannot be approved or changed again.";
            return false;
        }
        if (row.Status == ApprovalStatus.Approved)
        {
            why = "This record is ALREADY APPROVED. It cannot be approved a second time.";
            return false;
        }
        if (row.Status == ApprovalStatus.Rejected)
        {
            why = "This record was rejected and is back with the member. It can be decided again once they re-submit it.";
            return false;
        }
        why = null;
        return true;
    }

    /// <summary>
    /// Approval makes money payable, so it refuses a record that cannot actually
    /// be paid: a head with no payee, or nothing left to distribute.
    /// </summary>
    private static bool EnsurePayable(RemainingBvUpdate row, out string? why)
    {
        if (row.RemainingBV <= 0m)
        {
            why = "There is no remaining BV on this record to distribute, so it cannot be approved.";
            return false;
        }

        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(row.DiscomIncomeIdNo)) missing.Add("Discom Income");
        if (string.IsNullOrWhiteSpace(row.DealCloseIdNo)) missing.Add("Deal Close");
        if (string.IsNullOrWhiteSpace(row.SciIncomeIdNo)) missing.Add("SCI Income");

        if (missing.Count > 0)
        {
            why = $"{string.Join(", ", missing)} has no ID number against it. Correct the record before approving it.";
            return false;
        }

        why = null;
        return true;
    }

    /// <summary>
    /// Turns one Self/Other choice into the IdNo + name that gets stored. "Self"
    /// is the MEMBER, never the admin. An "Other" ID has to sit above the member
    /// in the sponsor tree — the same rule the user panel enforces, re-checked
    /// here because the admin's correction bypasses that screen entirely.
    /// </summary>
    private async Task<(string? IdNo, string? Name, string? Error)> ResolveHeadAsync(
        RemainingBvUpdate row, BvBeneficiaryMode mode, string? typedIdNo, string headLabel)
    {
        if (mode == BvBeneficiaryMode.Self)
            return (row.MemberIdNo, row.MemberName, null);

        var typed = (typedIdNo ?? string.Empty).Trim();
        if (typed.Length == 0)
            return (null, null, $"{headLabel}: 'Other' is selected, so an ID number must be entered.");

        var check = await _sponsorTree.CheckRelationAsync(row.MemberIdNo, typed);
        if (!check.IsAcceptable)
            return (null, null, $"{headLabel}: {check.Message}");

        return (check.IdNo, check.Name, null);
    }

    /// <summary>
    /// Runs Sp_UpdateReaminingBV for this record: the remaining BV goes into the
    /// legacy Repurchincome ledger and the row's SessID column is stamped with
    /// today's date in 112 (yyyymmdd) form.
    ///
    /// This deliberately happens BEFORE the row is marked Approved. Approval is
    /// irreversible for every actor, so a row that is frozen without its ledger
    /// entry would need a manual SQL fix; a row that is still Pending because the
    /// post failed just gets approved again once the cause is cleared.
    /// </summary>
    private Task<RemainingBvPostResult> PostToLedgerAsync(RemainingBvUpdate row) =>
        _bvIncome.PostAsync(row.Id, row.MemberIdNo, row.RemainingBV, LedgerRemark(row));

    /// <summary>
    /// DOUBLE-CLICK GUARD. Stamps PayoutPostedAt in ONE conditional UPDATE, so of
    /// two approve POSTs that arrive together for the same record exactly one
    /// wins — the loser matches no row, never reaches the ledger, and is told the
    /// record is already approved.
    ///
    /// EnsureEditable cannot do this on its own: it READS the row, and both
    /// requests read it as Pending before either of them writes. The claim has to
    /// BE the write, which is why this is a single UPDATE ... WHERE rather than a
    /// check followed by a save.
    ///
    /// Raw SQL because EF would send the whole tracked entity and lose the WHERE
    /// clause that is doing the locking. IsDeleted is repeated here because the
    /// entity's query filter does not apply to raw statements.
    /// </summary>
    private async Task<bool> ClaimForPostAsync(RemainingBvUpdate row, DateTime now)
    {
        var adminId = AdminId;

        var claimed = await _db.Database.ExecuteSqlInterpolatedAsync($@"
UPDATE RemainingBvUpdates
   SET PayoutPostedAt = {now}, UpdatedAt = {now}, UpdatedBy = {adminId}
 WHERE Id = {row.Id}
   AND IsDeleted = 0
   AND PayoutPostedAt IS NULL
   AND Status = {(int)ApprovalStatus.Pending}");

        // Keep the tracked entity in step with what the statement just wrote, or
        // the next SaveChangesAsync would push the stale NULL straight back over
        // the claim we just took.
        if (claimed == 1) row.PayoutPostedAt = now;
        return claimed == 1;
    }

    /// <summary>
    /// Gives the claim back so the record is approvable again. Used on two paths:
    /// the ledger post failed (the row must stay Pending and retryable), and
    /// verification mode, where posting the same record again is the whole point
    /// — the claim there only holds for the length of the post, which is exactly
    /// long enough to stop a double-click.
    /// </summary>
    private async Task ReleaseClaimAsync(RemainingBvUpdate row)
    {
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE RemainingBvUpdates SET PayoutPostedAt = NULL WHERE Id = {row.Id}");
        row.PayoutPostedAt = null;
    }

    /// <summary>
    /// Fills DiscomIncFormNo / DcloseIncFormNo / SciIncFormNo from the three
    /// heads' IdNo via m_membermaster, so a correction carries the right FormNo
    /// even before approval. Sp_UpdateReaminingBV does the same at approval.
    ///
    /// Raw SQL because FormNo is not on the MMemberMaster EF mapping and these
    /// three columns are not on the entity either — the user panel shares this
    /// table's mapping. The CAST keeps the m_membermaster.IDNo (varchar) lookup
    /// an index seek instead of converting the whole column to nvarchar.
    /// </summary>
    private Task SyncHeadFormNosAsync(RemainingBvUpdate row) =>
        _db.Database.ExecuteSqlInterpolatedAsync($@"
UPDATE r
   SET DiscomIncFormNo = (SELECT TOP 1 m.FormNo FROM m_membermaster m WHERE m.IDNo = CAST(r.DiscomIncomeIdNo AS varchar(50))),
       DcloseIncFormNo = (SELECT TOP 1 m.FormNo FROM m_membermaster m WHERE m.IDNo = CAST(r.DealCloseIdNo    AS varchar(50))),
       SciIncFormNo    = (SELECT TOP 1 m.FormNo FROM m_membermaster m WHERE m.IDNo = CAST(r.SciIncomeIdNo    AS varchar(50)))
  FROM RemainingBvUpdates r
 WHERE r.Id = {row.Id}");

    private const string AlreadyApprovedMessage =
        "This record is ALREADY APPROVED — the BV was posted just now, so nothing was done a second time.";

    /// <summary>
    /// What lands in Repurchincome.Remarks. The PLAN name ("1KW OFF GRID SYSTEM")
    /// comes first on purpose — it is the short label everyone recognises the
    /// record by. ProductName is the legacy order's full sales description
    /// ("SOLAR CONNECTION BOOKING WITH ADVANCED SOLAR CLEANING…"), which is only
    /// a fallback because a 150-char column cannot show it anyway.
    /// </summary>
    private static string LedgerRemark(RemainingBvUpdate row)
    {
        if (!string.IsNullOrWhiteSpace(row.PlanName)) return row.PlanName!.Trim();
        if (!string.IsNullOrWhiteSpace(row.ProductName)) return row.ProductName!.Trim();
        return $"Remaining BV {row.RequestNumber}".Trim();
    }

    /// <summary>
    /// Freezes the row. PayoutPostedAt is stamped in the SAME save as the status,
    /// because by this point the BV is already in Repurchincome — that stamp is
    /// what makes EnsureEditable refuse the row from here on, and it is the only
    /// thing standing between a replayed POST and a second BV credit.
    /// </summary>
    private void ApplyApproval(RemainingBvUpdate row, DateTime now)
    {
        row.Status = ApprovalStatus.Approved;
        row.ApprovedAt = now;
        row.ApprovedBy = AdminId;
        row.RejectionReason = null;
        row.RejectedAt = null;
        row.RejectedBy = null;
        row.PayoutPostedAt = now;
        row.UpdatedAt = now;
        row.UpdatedBy = AdminId;
    }

    /// <summary>
    /// Verification mode's end of the approve path: the BV is in Repurchincome
    /// and the SessID is stamped, but the row stays Pending. The member is NOT
    /// notified — nothing has been approved as far as they are concerned.
    /// </summary>
    private async Task LeftPendingAsync(RemainingBvUpdate row, RemainingBvPostResult posted, bool corrected)
    {
        await _activity.LogAsync(AdminId, "RemainingBv.PostOnly", "RemainingBvUpdate", row.Id.ToString(),
            $"Posted Remaining BV {row.RemainingBV:N2} for {row.MemberIdNo} ({row.RequestNumber}) to Repurchincome " +
            $"bill no {posted.BillNo}. Record left PENDING for verification " +
            $"(RemainingBv:StayPendingAfterPost)." + (corrected ? " Corrected before posting." : ""), ClientIp);
    }

    private async Task AfterApprovedAsync(RemainingBvUpdate row, bool corrected, RemainingBvPostResult posted)
    {
        await _activity.LogAsync(AdminId, "RemainingBv.Approve", "RemainingBvUpdate", row.Id.ToString(),
            $"Approved Remaining BV {row.RemainingBV:N2} (income {row.TotalIncome:N2}) for {row.MemberIdNo} " +
            $"({row.RequestNumber}). Discom Income {row.DiscomIncomeIdNo}, Deal Close {row.DealCloseIdNo}, " +
            $"SCI Income {row.SciIncomeIdNo}. Repurchincome bill no {posted.BillNo}." +
            (corrected ? " Corrected before approval." : ""), ClientIp);

        await NotifyMemberAsync(row, "Remaining BV approved",
            $"Your Remaining BV record ({row.RemainingBV:N2} BV) has been approved. " +
            $"The income is added to today's payout." +
            (string.IsNullOrWhiteSpace(row.AdminRemark) ? "" : $" Admin remark: {row.AdminRemark}"));
    }

    /// <summary>
    /// The record carries the member's legacy IdNo, not their portal login, so
    /// the notification target comes off the linked SolarRequest.
    /// </summary>
    private async Task NotifyMemberAsync(RemainingBvUpdate row, string title, string message)
    {
        var userId = await _db.SolarRequests
            .Where(r => r.Id == row.SolarRequestId)
            .Select(r => r.UserId)
            .FirstOrDefaultAsync();

        if (string.IsNullOrWhiteSpace(userId)) return;

        await _notifications.CreateAsync(new CreateNotificationDto
        {
            UserId = userId,
            SolarRequestId = row.SolarRequestId,
            Title = title,
            Message = message,
            NotificationType = "RemainingBv"
        });
    }
}
