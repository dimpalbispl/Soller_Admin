using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SolarPortal.Domain.Entities;
using SolarPortal.Domain.Enums;
using SolarPortal.Infrastructure.Data;

namespace SolarPortal.AdminWeb.Areas.SolarPanelAdmin.Controllers;

// Admin review of INC/Installer connections + withdrawals.
[Area("SolarPanelAdmin")]
[Authorize(Roles = "Admin")]
public class IncController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly SolarPortal.Application.Interfaces.Services.IAdminActivityLogger _activity;
    private readonly Microsoft.AspNetCore.Identity.UserManager<ApplicationUser> _userManager;

    public IncController(
        ApplicationDbContext db,
        SolarPortal.Application.Interfaces.Services.IAdminActivityLogger activity,
        Microsoft.AspNetCore.Identity.UserManager<ApplicationUser> userManager)
    {
        _db = db;
        _activity = activity;
        _userManager = userManager;
    }

    private string AdminId => _userManager.GetUserId(User) ?? "system";
    private string? ClientIp => HttpContext.Connection.RemoteIpAddress?.ToString();

    // ── INC connections (filter by state / city / status) ──
    public async Task<IActionResult> Connections(string? status, string? state, string? city)
    {
        var q = _db.IncConnections.Where(c => !c.IsDeleted);
        if (!string.IsNullOrWhiteSpace(status)) q = q.Where(c => c.Status == status);
        if (!string.IsNullOrWhiteSpace(state))  q = q.Where(c => c.State == state);
        if (!string.IsNullOrWhiteSpace(city))   q = q.Where(c => c.City == city);
        var list = await q.OrderByDescending(c => c.CreatedAt).ToListAsync();

        ViewBag.Status = status; ViewBag.State = state; ViewBag.City = city;
        ViewBag.States = await _db.IncConnections.Where(c => !c.IsDeleted && c.State != null).Select(c => c.State!).Distinct().OrderBy(x => x).ToListAsync();
        ViewBag.Cities = await _db.IncConnections.Where(c => !c.IsDeleted && c.City != null).Select(c => c.City!).Distinct().OrderBy(x => x).ToListAsync();
        var wids = list.Select(c => c.WorkerId).Distinct().ToList();
        var workers = await _db.Workers.Where(w => wids.Contains(w.Id)).ToListAsync();
        ViewBag.Workers = workers.ToDictionary(w => w.Id, w => w.Name);
        // Worker type (JOB / INC) — report mein type dikhane aur JOB ke liye
        // commission hide karne ke liye.
        ViewBag.WorkerTypes = workers.ToDictionary(w => w.Id, w => w.Type);
        return View(list);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ApproveConnection(int id, decimal commission, string? remark)
    {
        var c = await _db.IncConnections.FirstOrDefaultAsync(x => x.Id == id);
        if (c != null)
        {
            // Commission sirf INC-type worker ko milta hai — JOB worker ke
            // liye hamesha 0 (chahe form se kuch bhi aaye).
            var worker = await _db.Workers.FirstOrDefaultAsync(w => w.Id == c.WorkerId);
            if (worker == null || worker.Type != WorkerType.INC) commission = 0;

            c.Status = "Approved";
            c.CommissionAmount = commission;
            c.AdminRemark = remark;
            c.UpdatedAt = System.DateTime.UtcNow;
            await _db.SaveChangesAsync();
            TempData["Success"] = commission > 0
                ? $"Connection approved. Commission {commission:N0} set."
                : "Connection approved.";
        }
        return RedirectToAction(nameof(Connections));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RejectConnection(int id, string? remark)
    {
        var c = await _db.IncConnections.FirstOrDefaultAsync(x => x.Id == id);
        if (c != null)
        {
            c.Status = "Rejected";
            c.AdminRemark = remark;
            c.UpdatedAt = System.DateTime.UtcNow;
            await _db.SaveChangesAsync();
            TempData["Success"] = "Connection rejected.";
        }
        return RedirectToAction(nameof(Connections));
    }

    // ═══ INC KYC (change request point 8) ═════════════════════════════════
    // "INC — commission wale ka KYC upload system banana hai, approve by admin.
    //  Job wale ka KYC nahi dena hai."
    //
    // The INC uploads from the installer panel; this is the admin's approval
    // queue. Only commission-earning workers appear: WorkerType.INC. A JOB worker
    // is salaried, so their documents are never asked for and never listed —
    // the filter is on the worker's CURRENT type, so switching a worker's type
    // moves them in or out of this queue without leaving stale rows behind.
    //
    // One row per worker with THREE independently-verified sections (Address
    // Proof / Bank Detail / PAN Card), matching the legacy KYC.aspx layout and
    // the installer panel that writes these rows. Each section is approved or
    // rejected on its own, so a bad passbook does not send back a good PAN.
    public async Task<IActionResult> Kyc(string? status)
    {
        var incWorkerIds = await _db.Workers
            .Where(w => w.Type == WorkerType.INC && !w.IsDeleted)
            .Select(w => w.Id)
            .ToListAsync();

        var rows = await _db.IncKycDocuments
            .Where(k => incWorkerIds.Contains(k.WorkerId))
            .OrderByDescending(k => k.SubmittedAt ?? k.CreatedAt)
            .ToListAsync();

        // Filtering happens in memory: "pending" spans three status columns, which
        // is awkward to express in SQL and pointless at this row count.
        var f = (status ?? "pending").ToLowerInvariant();
        rows = f switch
        {
            "approved" => rows.Where(k => k.IsFullyApproved).ToList(),
            "rejected" => rows.Where(k => k.HasRejectedSection).ToList(),
            "all" => rows,
            _ => rows.Where(k => k.HasPendingSection).ToList()
        };

        ViewBag.Status = f;
        ViewBag.Workers = await _db.Workers
            .Where(w => incWorkerIds.Contains(w.Id))
            .ToDictionaryAsync(w => w.Id, w => w.Name);
        // Workers with no KYC on file at all — the admin needs to chase these,
        // and they are invisible if we only list submitted rows.
        ViewBag.WorkersWithoutKyc = await _db.Workers
            .Where(w => w.Type == WorkerType.INC && !w.IsDeleted &&
                        !_db.IncKycDocuments.Any(k => k.WorkerId == w.Id))
            .OrderBy(w => w.Name)
            .ToListAsync();
        return View(rows);
    }

    /// <summary>Sections the admin can decide on, and how each one is stamped.</summary>
    private static readonly string[] KycSections = { "address", "bank", "pan" };

    // POST: approve or reject ONE section of one worker's KYC.
    // `section` is address | bank | pan; `approve` picks the direction.
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ReviewKycSection(int id, string section, bool approve, string? remark)
    {
        var key = (section ?? "").Trim().ToLowerInvariant();
        if (!KycSections.Contains(key))
            return Json(new { success = false, message = "Unknown KYC section." });

        // A rejection has to say why — the installer only sees this text when
        // deciding what to re-upload.
        if (!approve && string.IsNullOrWhiteSpace(remark))
            return Json(new { success = false, message = "A rejection reason is required so the INC knows what to correct." });

        var doc = await _db.IncKycDocuments.FirstOrDefaultAsync(k => k.Id == id);
        if (doc == null) return Json(new { success = false, message = "KYC record not found." });

        // Guard against deciding a JOB worker's KYC: their type may have been
        // switched after submission, and only INC workers have KYC at all.
        var worker = await _db.Workers.FirstOrDefaultAsync(w => w.Id == doc.WorkerId);
        if (worker == null || worker.Type != WorkerType.INC)
            return Json(new { success = false, message = "KYC applies to INC (commission) workers only." });

        var decision = approve ? ApprovalStatus.Approved : ApprovalStatus.Rejected;
        var now = DateTime.UtcNow;

        // Refuse to decide a section the installer has not filled in yet —
        // approving an empty section would mark the worker verified on nothing.
        var (current, hasFile) = key switch
        {
            "address" => (doc.AddressStatus, !string.IsNullOrWhiteSpace(doc.AddressProofFrontPath)),
            "bank" => (doc.BankStatus, !string.IsNullOrWhiteSpace(doc.BankProofPath)),
            _ => (doc.PanStatus, !string.IsNullOrWhiteSpace(doc.PanProofPath))
        };

        if (!hasFile)
            return Json(new { success = false, message = $"The {key} section has not been submitted yet." });
        if (current == decision)
            return Json(new { success = false, message = $"This section is already {decision.ToString().ToLowerInvariant()}." });

        // An approval is final. The grid stops offering Reject once a section is
        // approved, but a tab left open from before the decision could still POST
        // one - and silently un-verifying a document the INC has already been told
        // is accepted is not something a stale click should be able to do.
        if (current == ApprovalStatus.Approved && decision == ApprovalStatus.Rejected)
            return Json(new { success = false, message = $"The {key} section is already approved. The INC has to resubmit that document before it can be reviewed again." });

        switch (key)
        {
            case "address":
                doc.AddressStatus = decision;
                doc.AddressRemark = remark;
                doc.AddressReviewedAt = now;
                doc.AddressReviewedBy = AdminId;
                break;
            case "bank":
                doc.BankStatus = decision;
                doc.BankRemark = remark;
                doc.BankReviewedAt = now;
                doc.BankReviewedBy = AdminId;
                break;
            default:
                doc.PanStatus = decision;
                doc.PanRemark = remark;
                doc.PanReviewedAt = now;
                doc.PanReviewedBy = AdminId;
                break;
        }

        doc.UpdatedAt = now;
        doc.UpdatedBy = AdminId;
        await _db.SaveChangesAsync();

        var label = key switch { "address" => "Address Proof", "bank" => "Bank Detail", _ => "PAN Card" };

        await _activity.LogAsync(AdminId, approve ? "IncKyc.Approve" : "IncKyc.Reject",
            "Worker", doc.WorkerId.ToString(),
            $"{(approve ? "Approved" : "Rejected")} {label} KYC for INC worker {worker.Name}." +
            (string.IsNullOrWhiteSpace(remark) ? "" : $" Remark: {remark}"), ClientIp);

        return Json(new
        {
            success = true,
            message = approve
                ? $"{label} approved for {worker.Name}." +
                  (doc.IsFullyApproved ? " All three sections are now approved." : "")
                : $"{label} rejected. {worker.Name} can correct just this section from their panel."
        });
    }

    // ── INC withdrawals ──
    public async Task<IActionResult> Withdrawals(string? status)
    {
        var q = _db.IncWithdrawals.AsQueryable();
        if (!string.IsNullOrWhiteSpace(status)) q = q.Where(w => w.Status == status);
        var list = await q.OrderByDescending(w => w.RequestedAt).ToListAsync();
        ViewBag.Status = status;
        var wids = list.Select(w => w.WorkerId).Distinct().ToList();
        ViewBag.Workers = await _db.Workers.Where(w => wids.Contains(w.Id)).ToDictionaryAsync(w => w.Id, w => w.Name);
        return View(list);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ApproveWithdrawal(int id, string? remark)
    {
        var w = await _db.IncWithdrawals.FirstOrDefaultAsync(x => x.Id == id);
        if (w != null && w.Status == "Pending")
        {
            w.Status = "Approved";
            w.ProcessedAt = System.DateTime.UtcNow;
            w.ProcessedBy = User.Identity?.Name;
            w.AdminNotes = remark;   // shown to the installer on their Withdraw page
            await _db.SaveChangesAsync();
            TempData["Success"] = $"Withdrawal of ₹{w.Amount:N2} approved.";
        }
        return RedirectToAction(nameof(Withdrawals));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RejectWithdrawal(int id, string? reason)
    {
        var w = await _db.IncWithdrawals.FirstOrDefaultAsync(x => x.Id == id);
        if (w != null && w.Status == "Pending")
        {
            // Status flip + wallet voucher must land together. If the voucher
            // insert fails we do NOT want a Rejected withdrawal with no credit
            // entry behind it, so both go in one transaction.
            await using var tx = await _db.Database.BeginTransactionAsync();

            w.Status = "Rejected";
            w.RejectionReason = reason;
            w.ProcessedAt = System.DateTime.UtcNow;
            w.ProcessedBy = User.Identity?.Name;
            await _db.SaveChangesAsync();

            await CreditIncWalletAsync(w, reason);

            await tx.CommitAsync();

            TempData["Success"] = $"Withdrawal rejected. ₹{w.Amount:N2} credited back to the INC wallet.";
        }
        return RedirectToAction(nameof(Withdrawals));
    }

    /// <summary>
    /// Writes the "money returned" credit into IncTrnvoucher — the INC wallet
    /// ledger (IncVouchertype: Acid 1, "INC Wallet", Actype 'I').
    ///
    /// Column conventions are copied from the existing TrnVoucher ledger:
    ///   • credit  → DrTo = '0', CrTo = account holder, VType = 'C'
    ///   • debit   → DrTo = account holder, CrTo = '0', VType = 'W' / 'D'
    ///   • AcType  → wallet the row belongs to; 'I' is the INC wallet
    ///   • VoucherId is IDENTITY; VoucherNo is not, so it is taken as MAX+1
    ///
    /// The account holder is the INC WORKER id. Every INC table
    /// (IncWithdrawals, IncConnections, IncCommissionLedger) is keyed by
    /// WorkerId, and a withdrawal carries nothing else that identifies the
    /// payee — there is no Worker → member Formno link in this schema.
    ///
    /// Raw SQL rather than EF: IncTrnvoucher is a legacy table with no entity,
    /// same as the other legacy writes in LegacyMlmApprovalService.
    /// </summary>
    private async Task CreditIncWalletAsync(IncWithdrawal w, string? reason)
    {
        var account   = w.WorkerId.ToString();
        var refNo     = string.IsNullOrWhiteSpace(w.RequestNumber) ? $"IWD-{w.Id}" : w.RequestNumber!;
        var narration = $"Withdrawal request {refNo} rejected — amount returned to INC wallet."
                      + (string.IsNullOrWhiteSpace(reason) ? "" : $" Reason: {reason.Trim()}");

        // Guarded by RefNo so a replayed reject can never credit twice. The
        // Status == "Pending" check above already prevents it; this is the
        // belt-and-braces version, because a double credit is real money.
        const string sql = @"
IF NOT EXISTS (SELECT 1 FROM IncTrnvoucher WHERE RefNo = @refNo AND VType = 'C' AND AcType = 'I')
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
        'I',
        GETDATE(),
        'C',
        CAST(CONVERT(varchar(8), GETDATE(), 112) AS numeric(18,0)),
        1,
        (SELECT ISNULL(SUM(CASE WHEN VType = 'C' AND CrTo = @account THEN Amount
                                WHEN VType <> 'C' AND DrTo = @account THEN -Amount
                                ELSE 0 END), 0)
           FROM IncTrnvoucher WHERE AcType = 'I') + @amount,
        0,
        NULL
    FROM IncTrnvoucher;
END";

        await _db.Database.ExecuteSqlRawAsync(sql,
            new Microsoft.Data.SqlClient.SqlParameter("@account",   account),
            new Microsoft.Data.SqlClient.SqlParameter("@amount",    w.Amount),
            new Microsoft.Data.SqlClient.SqlParameter("@narration", narration),
            new Microsoft.Data.SqlClient.SqlParameter("@refNo",     refNo));
    }
}
