using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SolarPortal.Application.Interfaces;
using SolarPortal.Application.Interfaces.Services;
using SolarPortal.Domain.Entities;
using SolarPortal.Domain.Enums;
using SolarPortal.Infrastructure.Data;

namespace SolarPortal.AdminWeb.Areas.SolarPanelAdmin.Controllers;

/// <summary>
/// Extra Payment Refund — money a member paid over and above what their project
/// needed, sent back to their wallet.
///
/// Two steps, like every other money movement here:
///   1. RAISE — pick the member (IdNo), the project, the wallet (voucher type)
///      and the amount. Nothing moves; the row is Pending.
///   2. DECIDE — approve or reject. ONLY approval writes the credit into the
///      legacy IncTrnvoucher ledger, tagged with the project's request number so
///      the refund can always be traced back to what it was for.
///
/// The ledger write follows the same column conventions as the INC commission
/// credit (IncCommissionCreditService) — credit rows are DrTo '0' / CrTo account
/// / VType 'C', VoucherNo is MAX+1 because it is not an identity column.
/// </summary>
[Area("SolarPanelAdmin")]
[Authorize(Roles = "Admin,SuperAdmin")]
public class RefundsController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly IUnitOfWork _uow;
    private readonly IPaymentService _payments;
    private readonly ISolarWalletService _solarWallet;
    private readonly IAdminActivityLogger _activity;
    private readonly UserManager<ApplicationUser> _userManager;

    public RefundsController(
        ApplicationDbContext db,
        IUnitOfWork uow,
        IPaymentService payments,
        ISolarWalletService solarWallet,
        IAdminActivityLogger activity,
        UserManager<ApplicationUser> userManager)
    {
        _db = db;
        _uow = uow;
        _payments = payments;
        _solarWallet = solarWallet;
        _activity = activity;
        _userManager = userManager;
    }

    private string AdminId => _userManager.GetUserId(User) ?? "system";
    private string? ClientIp => HttpContext.Connection.RemoteIpAddress?.ToString();

    // GET: /SolarPanelAdmin/Refunds?status=all|pending|approved|rejected
    public async Task<IActionResult> Index(string? status)
    {
        var f = (status ?? "all").Trim().ToLowerInvariant();

        var q = _db.ExtraPaymentRefunds.AsNoTracking();
        q = f switch
        {
            "pending"  => q.Where(x => x.Status == ApprovalStatus.Pending),
            "approved" => q.Where(x => x.Status == ApprovalStatus.Approved),
            "rejected" => q.Where(x => x.Status == ApprovalStatus.Rejected),
            _ => q
        };

        // Newest first, like every other queue in this panel.
        var rows = await q.OrderByDescending(x => x.RequestedAt ?? x.CreatedAt)
                          .ThenByDescending(x => x.Id)
                          .ToListAsync();

        ViewBag.Status = f;
        ViewBag.PendingCount  = await _db.ExtraPaymentRefunds.CountAsync(x => x.Status == ApprovalStatus.Pending);
        ViewBag.ApprovedCount = await _db.ExtraPaymentRefunds.CountAsync(x => x.Status == ApprovalStatus.Approved);
        ViewBag.RejectedCount = await _db.ExtraPaymentRefunds.CountAsync(x => x.Status == ApprovalStatus.Rejected);
        ViewBag.VoucherTypes  = await _solarWallet.GetWalletsAsync();
        ViewBag.MyId = AdminId;
        return View(rows);
    }

    // GET: /SolarPanelAdmin/Refunds/Lookup?memberId=TESTSUNIL
    //
    // Resolves the typed Member ID to that member's projects, with what each one
    // has actually received — the admin needs to see the overpaid figure before
    // typing an amount, not after.
    [HttpGet]
    public async Task<IActionResult> Lookup(string? memberId)
    {
        var id = (memberId ?? string.Empty).Trim();
        if (id.Length == 0)
            return Json(new { found = false, message = "Enter a Member ID." });

        var requests = (await _uow.SolarRequests.FindAsync(r =>
                           r.UserId != null && r.UserId.Trim().ToUpper() == id.ToUpper()))
                       .OrderByDescending(r => r.CreatedAt)
                       .ToList();

        if (requests.Count == 0)
            return Json(new { found = false, message = $"No solar request found for {id}." });

        var list = new List<object>();
        foreach (var r in requests)
        {
            var paid = await _payments.GetVerifiedPaidAsync(r.Id);
            var extra = paid - r.PlanAmount;          // > 0 means the member overpaid
            list.Add(new
            {
                id = r.Id,
                requestNumber = r.RequestNumber,
                plan = r.SelectedPlan,
                total = r.PlanAmount,
                paid,
                extra = extra > 0 ? extra : 0m,
                stage = r.CurrentStage.ToString()
            });
        }

        var first = requests[0];
        var name = string.IsNullOrWhiteSpace(first.MemberFullName) ? first.ApplicantName : first.MemberFullName;
        return Json(new { found = true, memberId = id, name, mobile = first.MobileNumber, requests = list });
    }

    // POST: /SolarPanelAdmin/Refunds/Create
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(int solarRequestId, string memberIdNo, decimal amount,
                                            int voucherTypeId, string? entryType, string? remark)
    {
        if (amount <= 0)
            return Json(new { success = false, message = "Amount must be greater than zero." });

        // "C" pays the member, "D" takes it back. Anything else is a bad post.
        var type = string.Equals(entryType, "D", StringComparison.OrdinalIgnoreCase) ? "D" : "C";
        if (string.IsNullOrWhiteSpace(memberIdNo))
            return Json(new { success = false, message = "Member ID is required." });

        var req = await _uow.SolarRequests.GetByIdAsync(solarRequestId);
        if (req == null)
            return Json(new { success = false, message = "Solar request not found." });

        var vtypes = await _solarWallet.GetWalletsAsync();
        var vtype = vtypes.FirstOrDefault(v => v.Acid == voucherTypeId);
        if (vtype == null)
            return Json(new { success = false, message = "Choose a voucher type (wallet)." });

        // One open entry per project PER DIRECTION: two pending credits on the same
        // project are almost always the same refund entered twice, and both would be
        // approvable. A credit and a debit can legitimately be open together.
        var alreadyOpen = await _db.ExtraPaymentRefunds
            .AnyAsync(x => x.SolarRequestId == solarRequestId
                        && x.EntryType == type
                        && x.Status == ApprovalStatus.Pending);
        if (alreadyOpen)
            return Json(new { success = false, message = $"A {(type == "D" ? "debit" : "credit")} fund transfer for this project is already waiting for a decision." });

        var memberName = string.IsNullOrWhiteSpace(req.MemberFullName) ? req.ApplicantName : req.MemberFullName;

        var row = new ExtraPaymentRefund
        {
            SolarRequestId  = solarRequestId,
            RequestNumber   = req.RequestNumber,
            MemberIdNo      = memberIdNo.Trim(),
            MemberName      = memberName,
            Amount          = amount,
            EntryType       = type,
            VoucherTypeId   = vtype.Acid,
            VoucherAcType   = vtype.Actype,
            VoucherTypeName = vtype.WalletName,
            Remark          = remark?.Trim(),
            Status          = ApprovalStatus.Pending,
            RequestedBy     = AdminId,
            RequestedAt     = DateTime.UtcNow
        };

        _db.ExtraPaymentRefunds.Add(row);
        await _db.SaveChangesAsync();

        var word = type == "D" ? "debit" : "credit";
        await _activity.LogAsync(AdminId, "Refund.Create", "Refund", row.Id.ToString(),
            $"Raised fund transfer ({word}) ₹{amount:N0} for {memberIdNo} on {req.RequestNumber} " +
            $"({vtype.WalletName}). Awaiting approval.", ClientIp);

        return Json(new { success = true, message = $"{char.ToUpper(word[0])}{word[1..]} of ₹{amount:N0} raised for {req.RequestNumber}. It is waiting for approval." });
    }

    // POST: /SolarPanelAdmin/Refunds/Approve
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Approve(int id, string? note)
    {
        var row = await _db.ExtraPaymentRefunds.FirstOrDefaultAsync(x => x.Id == id);
        if (row == null) return Json(new { success = false, message = "Entry not found." });
        if (row.Status == ApprovalStatus.Approved || row.PostedAt != null)
            return Json(new { success = false, message = "This entry is already approved." });
        if (row.Status == ApprovalStatus.Rejected)
            return Json(new { success = false, message = "This entry was rejected. Raise a fresh one instead." });

        var refNo = $"FT/{row.RequestNumber}/{row.Id}";
        var narration = (row.IsCredit
                            ? $"Fund transfer credited for {row.RequestNumber} · Member ID {row.MemberIdNo}"
                            : $"Fund transfer debited against {row.RequestNumber} · Member ID {row.MemberIdNo}")
                      + (string.IsNullOrWhiteSpace(row.Remark) ? "" : $". {row.Remark}");

        // The ledger row first: if this throws, the refund stays Pending and can be
        // approved again. Marking it approved first would leave a refund that says
        // "paid" with no money behind it.
        await PostVoucherAsync(row, refNo, narration);

        row.Status          = ApprovalStatus.Approved;
        row.DecidedBy       = AdminId;
        row.DecidedAt       = DateTime.UtcNow;
        row.VoucherRefNo    = refNo;
        row.PostedAt        = DateTime.UtcNow;
        if (!string.IsNullOrWhiteSpace(note))
            row.Remark = string.IsNullOrWhiteSpace(row.Remark) ? note.Trim() : $"{row.Remark} | {note.Trim()}";
        row.UpdatedAt = DateTime.UtcNow;
        row.UpdatedBy = AdminId;
        await _db.SaveChangesAsync();

        var done = row.IsCredit ? "Credited to" : "Debited from";
        await _activity.LogAsync(AdminId, "Refund.Approve", "Refund", row.Id.ToString(),
            $"Approved fund transfer ({(row.IsCredit ? "credit" : "debit")}) ₹{row.Amount:N0} for {row.MemberIdNo} " +
            $"on {row.RequestNumber}. {done} {row.VoucherTypeName} (ref {refNo}).", ClientIp);

        return Json(new
        {
            success = true,
            message = row.IsCredit
                ? $"₹{row.Amount:N0} credited to {row.MemberIdNo} in {row.VoucherTypeName}."
                : $"₹{row.Amount:N0} debited from {row.MemberIdNo}'s {row.VoucherTypeName}."
        });
    }

    // POST: /SolarPanelAdmin/Refunds/Reject
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Reject(int id, string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return Json(new { success = false, message = "A rejection reason is required." });

        var row = await _db.ExtraPaymentRefunds.FirstOrDefaultAsync(x => x.Id == id);
        if (row == null) return Json(new { success = false, message = "Entry not found." });
        if (row.Status == ApprovalStatus.Approved || row.PostedAt != null)
            return Json(new { success = false, message = "This entry is already approved and cannot be rejected." });
        if (row.Status == ApprovalStatus.Rejected)
            return Json(new { success = false, message = "This entry is already rejected." });

        row.Status          = ApprovalStatus.Rejected;
        row.RejectionReason = reason.Trim();
        row.DecidedBy       = AdminId;
        row.DecidedAt       = DateTime.UtcNow;
        row.UpdatedAt       = DateTime.UtcNow;
        row.UpdatedBy       = AdminId;
        await _db.SaveChangesAsync();

        await _activity.LogAsync(AdminId, "Refund.Reject", "Refund", row.Id.ToString(),
            $"Rejected fund transfer #{row.Id} (₹{row.Amount:N0}, {row.MemberIdNo}). Reason: {reason}", ClientIp);

        return Json(new { success = true, message = "Fund transfer rejected. No money was moved." });
    }

    /// <summary>
    /// Writes the entry into the member's SOLAR wallet ledger (SolarTrnvoucher) in
    /// whichever direction the row says - credit pays the member, debit takes it
    /// back. The INC wallet is deliberately NOT touched: that one belongs to the
    /// installer's commission, and solar money is the member's.
    ///
    /// SolarWalletService guards on RefNo + direction, so a replayed approve can
    /// never post twice (PostedAt on the row is the first guard).
    /// </summary>
    private async Task PostVoucherAsync(ExtraPaymentRefund row, string refNo, string narration)
    {
        await _solarWallet.PostAsync(row.MemberIdNo, row.Amount, row.IsCredit, refNo, narration);
    }
}
