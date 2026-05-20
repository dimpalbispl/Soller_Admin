using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SolarPortal.Application.Interfaces;
using SolarPortal.Domain.Entities;
using SolarPortal.Domain.Enums;

namespace SolarPortal.AdminWeb.Areas.SolarPanelAdmin.Controllers;

/// <summary>
/// INC Commission management — view, create, assign to an INC worker, mark as paid.
/// Per spec: "INC Commission management add karo."
/// </summary>
[Area("SolarPanelAdmin")]
[Authorize(Roles = "Admin")]
public class CommissionController : Controller
{
    private readonly IUnitOfWork _uow;
    private readonly UserManager<ApplicationUser> _userManager;

    // Default percentage applied when admin generates a commission without specifying one.
    // Can be overridden per-record on the Create form.
    private const decimal DefaultCommissionPct = 5m;

    public CommissionController(IUnitOfWork uow, UserManager<ApplicationUser> userManager)
    {
        _uow = uow;
        _userManager = userManager;
    }

    // GET: /SolarPanelAdmin/Commission
    public async Task<IActionResult> Index(string? filter)
    {
        var query = _uow.Commissions.Query().Include(c => c.Worker).Include(c => c.SolarRequest);
        IQueryable<Commission> q = filter switch
        {
            "paid"    => query.Where(c => c.IsPaid),
            "pending" => query.Where(c => !c.IsPaid),
            _         => query
        };

        var rows = await q.OrderByDescending(c => c.CreatedAt).ToListAsync();
        ViewBag.Filter = filter ?? "all";
        ViewBag.TotalPending = await _uow.Commissions.Query().Where(c => !c.IsPaid).SumAsync(c => c.CommissionAmount);
        ViewBag.TotalPaid    = await _uow.Commissions.Query().Where(c => c.IsPaid).SumAsync(c => c.CommissionAmount);

        // Workers (INC type only) so the assign-on-create modal can offer them
        ViewBag.IncWorkers = (await _uow.Workers.FindAsync(w => w.Type == WorkerType.INC))
                             .OrderBy(w => w.Name).ToList();
        // Completed requests that don't yet have a commission row, for quick generation
        var existingReqIds = await _uow.Commissions.Query().Select(c => c.SolarRequestId).ToListAsync();
        var existingSet = new HashSet<int>(existingReqIds);
        var completedNoComm = (await _uow.SolarRequests.FindAsync(r => r.CurrentStage == ProjectStatus.Completed))
                              .Where(r => !existingSet.Contains(r.Id))
                              .OrderByDescending(r => r.UpdatedAt ?? r.CreatedAt)
                              .ToList();
        ViewBag.UncommissionedCompleted = completedNoComm;

        return View(rows);
    }

    // POST: create or upsert a commission for a request
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(int solarRequestId, int? workerId, decimal? percentage, string? notes)
    {
        try
        {
            var req = await _uow.SolarRequests.GetByIdAsync(solarRequestId);
            if (req == null)
                return Json(new { success = false, message = "Request not found." });

            // Prevent duplicate commission for the same request — one commission per project.
            var existing = (await _uow.Commissions.FindAsync(c => c.SolarRequestId == solarRequestId))
                            .FirstOrDefault();
            if (existing != null)
                return Json(new { success = false, message = $"Commission already exists for {req.RequestNumber}." });

            var pct = percentage.GetValueOrDefault(DefaultCommissionPct);
            if (pct <= 0 || pct > 100)
                return Json(new { success = false, message = "Commission percentage must be between 0 and 100." });

            // Validate worker if provided
            Worker? worker = null;
            if (workerId.HasValue)
            {
                worker = await _uow.Workers.GetByIdAsync(workerId.Value);
                if (worker == null)
                    return Json(new { success = false, message = "Worker not found." });
                if (worker.Type != WorkerType.INC)
                    return Json(new { success = false, message = "Only INC-type workers can earn commission." });
            }

            var amount = Math.Round(req.PlanAmount * pct / 100m, 2);

            var commission = new Commission
            {
                SolarRequestId = solarRequestId,
                UserId = req.UserId,
                WorkerId = workerId,
                ProjectAmount = req.PlanAmount,
                CommissionPercentage = pct,
                CommissionAmount = amount,
                IsPaid = false,
                Notes = notes
            };
            await _uow.Commissions.AddAsync(commission);
            await _uow.SaveChangesAsync();

            return Json(new
            {
                success = true,
                message = $"Commission of ₹{amount:N0} created for {req.RequestNumber}" +
                          (worker != null ? $" (assigned to {worker.Name})" : "") + "."
            });
        }
        catch (Exception ex)
        {
            var detail = ex.InnerException?.Message ?? ex.Message;
            return Json(new { success = false, message = $"Create failed: {detail}" });
        }
    }

    // POST: mark commission paid
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MarkPaid(int id, string? reference)
    {
        try
        {
            var c = await _uow.Commissions.GetByIdAsync(id);
            if (c == null) return Json(new { success = false, message = "Commission not found." });

            // Idempotency: don't allow double-payment marking
            if (c.IsPaid)
                return Json(new { success = false, message = "Commission is already marked as paid." });

            c.IsPaid = true;
            c.PaidAt = DateTime.UtcNow;
            c.PaidBy = _userManager.GetUserId(User) ?? "system";
            c.PaymentReference = reference;
            _uow.Commissions.Update(c);
            await _uow.SaveChangesAsync();

            return Json(new { success = true, message = $"Commission of ₹{c.CommissionAmount:N0} marked as paid." });
        }
        catch (Exception ex)
        {
            var detail = ex.InnerException?.Message ?? ex.Message;
            return Json(new { success = false, message = $"Mark paid failed: {detail}" });
        }
    }

    // POST: assign / re-assign worker
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AssignWorker(int id, int workerId)
    {
        try
        {
            var c = await _uow.Commissions.GetByIdAsync(id);
            if (c == null) return Json(new { success = false, message = "Commission not found." });
            if (c.IsPaid)  return Json(new { success = false, message = "Cannot reassign a paid commission." });

            var w = await _uow.Workers.GetByIdAsync(workerId);
            if (w == null) return Json(new { success = false, message = "Worker not found." });
            if (w.Type != WorkerType.INC)
                return Json(new { success = false, message = "Only INC-type workers can earn commission." });

            c.WorkerId = workerId;
            _uow.Commissions.Update(c);
            await _uow.SaveChangesAsync();
            return Json(new { success = true, message = $"Assigned to {w.Name}." });
        }
        catch (Exception ex)
        {
            var detail = ex.InnerException?.Message ?? ex.Message;
            return Json(new { success = false, message = $"Assign failed: {detail}" });
        }
    }
}
