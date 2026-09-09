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
    private readonly IAdminActivityLogger _activity;
    private readonly UserManager<ApplicationUser> _userManager;

    public RefundsController(
        ApplicationDbContext db,
        IUnitOfWork uow,
        IPaymentService payments,
        IAdminActivityLogger activity,
        UserManager<ApplicationUser> userManager)
    {
        _db = db;
        _uow = uow;
        _payments = payments;
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
        ViewBag.VoucherTypes  = await LoadVoucherTypesAsync();
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
                                            int voucherTypeId, string? remark)
    {
        if (amount <= 0)
            return Json(new { success = false, message = "Amount must be greater than zero." });
        if (string.IsNullOrWhiteSpace(memberIdNo))
            return Json(new { success = false, message = "Member ID is required." });

        var req = await _uow.SolarRequests.GetByIdAsync(solarRequestId);
        if (req == null)
            return Json(new { success = false, message = "Solar request not found." });

        var vtypes = await LoadVoucherTypesAsync();
        var vtype = vtypes.FirstOrDefault(v => v.Acid == voucherTypeId);
        if (vtype == null)
            return Json(new { success = false, message = "Choose a voucher type (wallet)." });

        // One open refund per project at a time: two pending rows for the same
        // project are almost always the same refund entered twice, and both would
        // be approvable.
        var alreadyOpen = await _db.ExtraPaymentRefunds
            .AnyAsync(x => x.SolarRequestId == solarRequestId && x.Status == ApprovalStatus.Pending);
        if (alreadyOpen)
            return Json(new { success = false, message = "A refund for this project is already waiting for a decision." });

        var memberName = string.IsNullOrWhiteSpace(req.MemberFullName) ? req.ApplicantName : req.MemberFullName;

        var row = new ExtraPaymentRefund
        {
            SolarRequestId  = solarRequestId,
            RequestNumber   = req.RequestNumber,
            MemberIdNo      = memberIdNo.Trim(),
            MemberName      = memberName,
            Amount          = amount,
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

        await _activity.LogAsync(AdminId, "Refund.Create", "Refund", row.Id.ToString(),
            $"Raised extra-payment refund ₹{amount:N0} for {memberIdNo} on {req.RequestNumber} " +
            $"({vtype.WalletName}). Awaiting approval.", ClientIp);

        return Json(new { success = true, message = $"Refund of ₹{amount:N0} raised for {req.RequestNumber}. It is waiting for approval." });
    }

    // POST: /SolarPanelAdmin/Refunds/Approve
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Approve(int id, string? note)
    {
        var row = await _db.ExtraPaymentRefunds.FirstOrDefaultAsync(x => x.Id == id);
        if (row == null) return Json(new { success = false, message = "Refund not found." });
        if (row.Status == ApprovalStatus.Approved || row.PostedAt != null)
            return Json(new { success = false, message = "This refund is already approved." });
        if (row.Status == ApprovalStatus.Rejected)
            return Json(new { success = false, message = "This refund was rejected. Raise a fresh one instead." });

        var refNo = $"REFUND/{row.RequestNumber}/{row.Id}";
        var narration = $"Extra payment refund for {row.RequestNumber} · Member ID {row.MemberIdNo}"
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

        await _activity.LogAsync(AdminId, "Refund.Approve", "Refund", row.Id.ToString(),
            $"Approved extra-payment refund ₹{row.Amount:N0} for {row.MemberIdNo} on {row.RequestNumber}. " +
            $"Credited to {row.VoucherTypeName} (ref {refNo}).", ClientIp);

        return Json(new { success = true, message = $"₹{row.Amount:N0} refunded to {row.MemberIdNo} in {row.VoucherTypeName}." });
    }

    // POST: /SolarPanelAdmin/Refunds/Reject
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Reject(int id, string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return Json(new { success = false, message = "A rejection reason is required." });

        var row = await _db.ExtraPaymentRefunds.FirstOrDefaultAsync(x => x.Id == id);
        if (row == null) return Json(new { success = false, message = "Refund not found." });
        if (row.Status == ApprovalStatus.Approved || row.PostedAt != null)
            return Json(new { success = false, message = "This refund is already approved and cannot be rejected." });
        if (row.Status == ApprovalStatus.Rejected)
            return Json(new { success = false, message = "This refund is already rejected." });

        row.Status          = ApprovalStatus.Rejected;
        row.RejectionReason = reason.Trim();
        row.DecidedBy       = AdminId;
        row.DecidedAt       = DateTime.UtcNow;
        row.UpdatedAt       = DateTime.UtcNow;
        row.UpdatedBy       = AdminId;
        await _db.SaveChangesAsync();

        await _activity.LogAsync(AdminId, "Refund.Reject", "Refund", row.Id.ToString(),
            $"Rejected extra-payment refund #{row.Id} (₹{row.Amount:N0}, {row.MemberIdNo}). Reason: {reason}", ClientIp);

        return Json(new { success = true, message = "Refund rejected. No money was moved." });
    }

    /// <summary>
    /// Writes the refund credit into IncTrnvoucher — the same ledger the INC
    /// commission is written to. Guarded on RefNo so a replayed approve can never
    /// credit twice (PostedAt above is the first guard; this is the second,
    /// because a double credit is real money).
    /// </summary>
    private async Task PostVoucherAsync(ExtraPaymentRefund row, string refNo, string narration)
    {
        const string sql = @"
IF NOT EXISTS (SELECT 1 FROM IncTrnvoucher WHERE RefNo = @refNo AND VType = 'C')
BEGIN
    INSERT INTO IncTrnvoucher
        (VoucherNo, VoucherDate, DrTo, CrTo, Amount, Narration, RefNo,
         AcType, RecTimeStamp, VType, SessID, WSessID, Balance, UserId, FromID)
    SELECT
        ISNULL(MAX(VoucherNo), 0) + 1,
        CAST(CONVERT(varchar(8), GETDATE(), 112) AS datetime),
        '0',
        @account,
        @amount,
        @narration,
        @refNo,
        @acType,
        GETDATE(),
        'C',
        CAST(CONVERT(varchar(8), GETDATE(), 112) AS numeric(18,0)),
        1,
        0,
        0,
        NULL
    FROM IncTrnvoucher;
END";

        await _db.Database.ExecuteSqlRawAsync(sql,
            new Microsoft.Data.SqlClient.SqlParameter("@account",   row.MemberIdNo),
            new Microsoft.Data.SqlClient.SqlParameter("@amount",    row.Amount),
            new Microsoft.Data.SqlClient.SqlParameter("@narration", narration),
            new Microsoft.Data.SqlClient.SqlParameter("@refNo",     refNo),
            new Microsoft.Data.SqlClient.SqlParameter("@acType",    row.VoucherAcType));
    }

    /// <summary>The wallets a refund can be credited to — read from IncVouchertype.</summary>
    private async Task<List<VoucherTypeRow>> LoadVoucherTypesAsync()
    {
        var list = new List<VoucherTypeRow>();
        var conn = _db.Database.GetDbConnection();
        var opened = false;
        try
        {
            if (conn.State != System.Data.ConnectionState.Open) { await conn.OpenAsync(); opened = true; }
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Acid, WalletName, Actype FROM IncVouchertype WHERE ISNULL(ActiveStatus,'Y') = 'Y' ORDER BY Acid";
            await using var rd = await cmd.ExecuteReaderAsync();
            while (await rd.ReadAsync())
            {
                list.Add(new VoucherTypeRow(
                    Convert.ToInt32(rd["Acid"]),
                    (rd["WalletName"]?.ToString() ?? "").Trim(),
                    (rd["Actype"]?.ToString() ?? "I").Trim()));
            }
        }
        catch
        {
            // Legacy table unreachable — the page still renders, with no wallet to
            // pick, rather than 500ing.
        }
        finally
        {
            if (opened) await conn.CloseAsync();
        }
        return list;
    }

    public record VoucherTypeRow(int Acid, string WalletName, string Actype);
}
