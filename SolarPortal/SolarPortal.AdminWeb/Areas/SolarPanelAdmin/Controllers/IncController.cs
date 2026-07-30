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
    public IncController(ApplicationDbContext db) { _db = db; }

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
