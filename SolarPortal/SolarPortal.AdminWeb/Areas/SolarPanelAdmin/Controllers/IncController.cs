using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
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
        ViewBag.Workers = await _db.Workers.Where(w => wids.Contains(w.Id)).ToDictionaryAsync(w => w.Id, w => w.Name);
        return View(list);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ApproveConnection(int id, decimal commission, string? remark)
    {
        var c = await _db.IncConnections.FirstOrDefaultAsync(x => x.Id == id);
        if (c != null)
        {
            c.Status = "Approved";
            c.CommissionAmount = commission;
            c.AdminRemark = remark;
            c.UpdatedAt = System.DateTime.UtcNow;
            await _db.SaveChangesAsync();
            TempData["Success"] = $"Connection approved. Commission {commission:N0} set.";
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
    public async Task<IActionResult> ApproveWithdrawal(int id)
    {
        var w = await _db.IncWithdrawals.FirstOrDefaultAsync(x => x.Id == id);
        if (w != null && w.Status == "Pending")
        {
            w.Status = "Approved";
            w.ProcessedAt = System.DateTime.UtcNow;
            w.ProcessedBy = User.Identity?.Name;
            await _db.SaveChangesAsync();
            TempData["Success"] = "Withdrawal approved.";
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
            w.Status = "Rejected";
            w.RejectionReason = reason;
            w.ProcessedAt = System.DateTime.UtcNow;
            w.ProcessedBy = User.Identity?.Name;
            await _db.SaveChangesAsync();
            TempData["Success"] = "Withdrawal rejected.";
        }
        return RedirectToAction(nameof(Withdrawals));
    }
}