using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SolarPortal.Application.Interfaces;
using SolarPortal.Domain.Entities;
using SolarPortal.Domain.Enums;

namespace SolarPortal.AdminWeb.Areas.SolarPanelAdmin.Controllers;

/// <summary>
/// INC Commission management.
///
/// Model (per admin spec, 2026-07-29):
///   • Commission is configured PER SOLAR PLAN as a FLAT RUPEE AMOUNT — not a
///     percentage. Admin picks a plan from the SolarProjects master ("5 kW Solar
///     Kit") and sets, say, ₹2,000. Every INC worker earns exactly that on that
///     plan, regardless of the project's value. It is stored right on the plan
///     row: SolarProjects.IncCommissionAmount.
///   • Payout records are generated AUTOMATICALLY: any member project that is on
///     a plan with a commission amount and already has an INC worker assigned
///     gets a commission row the next time this page loads (or right after an
///     amount is saved).
///   • Money still can't move early — a commission is only payable once the
///     project reaches Completed.
/// </summary>
[Area("SolarPanelAdmin")]
[Authorize(Roles = "Admin")]
public class CommissionController : Controller
{
    private readonly IUnitOfWork _uow;
    private readonly UserManager<ApplicationUser> _userManager;

    // Auto-generation runs on every page load AND right after an amount is
    // saved. Without serialisation two overlapping requests can both see "no
    // commission yet" for the same project and each insert a row — there is no
    // unique constraint on Commissions.SolarRequestId to catch it. One-at-a-time
    // makes the second caller re-read the rows the first just wrote.
    private static readonly SemaphoreSlim AutoGenLock = new(1, 1);

    public CommissionController(IUnitOfWork uow, UserManager<ApplicationUser> userManager)
    {
        _uow = uow;
        _userManager = userManager;
    }

    // GET: /SolarPanelAdmin/Commission
    public async Task<IActionResult> Index(string? filter)
    {
        // Bring payouts up to date before rendering. Non-fatal: a failure here
        // must not blank out the page, the admin can still see existing records.
        var autoCreated = 0;
        try { autoCreated = await AutoGeneratePayoutsAsync(); }
        catch { /* ignored — surfaced as "0 new" */ }
        ViewBag.AutoCreated = autoCreated;

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

        var plans = (await _uow.SolarProjects.FindAsync(p => p.IsActive))
                    .OrderBy(p => p.SolarTypeKV).ToList();
        ViewBag.Plans = plans;
        ViewBag.PlanAmounts = BuildAmountMap(plans);

        ViewBag.IncWorkers = (await _uow.Workers.FindAsync(w => w.Type == WorkerType.INC))
                             .OrderBy(w => w.Name).ToList();

        // Projects that are assigned + uncommissioned but whose plan has no
        // amount yet — these are the ones the admin still needs to act on.
        ViewBag.WaitingForRate = await CountWaitingForRateAsync();

        return View(rows);
    }

    // POST: set the flat commission amount for one solar plan
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SavePlanCommission(int solarProjectId, decimal amount)
    {
        if (amount <= 0)
            return Json(new { success = false, message = "Commission amount must be greater than 0." });

        var plan = await _uow.SolarProjects.GetByIdAsync(solarProjectId);
        if (plan == null)
            return Json(new { success = false, message = "Solar project not found." });

        // A commission larger than the project itself is a typo every time
        // (₹300000 for ₹30000). Cheap guard against a very expensive slip.
        if (plan.ProjectAmount > 0 && amount > plan.ProjectAmount)
        {
            return Json(new
            {
                success = false,
                message = $"₹{amount:N0} is more than the project value (₹{plan.ProjectAmount:N0}). " +
                          "Please check the amount again."
            });
        }

        plan.IncCommissionAmount = amount;
        plan.UpdatedAt = DateTime.UtcNow;
        plan.UpdatedBy = _userManager.GetUserId(User);
        _uow.SolarProjects.Update(plan);
        await _uow.SaveChangesAsync();

        // Apply it straight away to anything already waiting on this plan.
        var created = 0;
        try { created = await AutoGeneratePayoutsAsync(); } catch { /* non-fatal */ }

        return Json(new
        {
            success = true,
            message = $"{plan.Name}: commission ₹{amount:N0} per project set. " +
                      (created > 0
                          ? $"{created} payout record(s) generated for already-assigned projects."
                          : "No assigned project is waiting on this plan right now.")
        });
    }

    /// <summary>planId → flat commission ₹, for plans that actually have one set.</summary>
    private static Dictionary<int, decimal> BuildAmountMap(IEnumerable<SolarProject> plans) =>
        plans.Where(p => p.IncCommissionAmount.HasValue && p.IncCommissionAmount.Value > 0)
             .ToDictionary(p => p.Id, p => p.IncCommissionAmount!.Value);

    private async Task<Dictionary<int, decimal>> LoadPlanAmountsAsync() =>
        BuildAmountMap(await _uow.SolarProjects.GetAllAsync());

    /// <summary>
    /// requestId → assigned INC worker id. Installation's worker wins over the
    /// Material Dispatch one (it is the later, more specific assignment).
    /// Non-INC workers are dropped — only INC earns commission.
    /// </summary>
    private async Task<Dictionary<int, int>> BuildAssignedIncWorkerMapAsync()
    {
        var incIds = (await _uow.Workers.FindAsync(w => w.Type == WorkerType.INC))
                     .Select(w => w.Id).ToHashSet();
        var map = new Dictionary<int, int>();

        foreach (var m in (await _uow.MaterialDispatches.GetAllAsync())
                          .Where(x => x.AssignedWorkerId.HasValue)
                          .OrderBy(x => x.CreatedAt))
        {
            if (incIds.Contains(m.AssignedWorkerId!.Value))
                map[m.SolarRequestId] = m.AssignedWorkerId.Value;
        }

        foreach (var i in (await _uow.Installations.GetAllAsync())
                          .Where(x => x.AssignedWorkerId.HasValue)
                          .OrderBy(x => x.CreatedAt))
        {
            if (incIds.Contains(i.AssignedWorkerId!.Value))
                map[i.SolarRequestId] = i.AssignedWorkerId.Value;
        }

        return map;
    }

    private async Task<int> AutoGeneratePayoutsAsync()
    {
        await AutoGenLock.WaitAsync();
        try
        {
            return await AutoGeneratePayoutsCoreAsync();
        }
        finally
        {
            AutoGenLock.Release();
        }
    }

    /// <summary>
    /// Creates a payout row for every member project that is (a) on a plan with
    /// a commission amount, (b) already has an INC worker assigned, and (c) has
    /// no commission row yet. Returns how many were created.
    /// </summary>
    private async Task<int> AutoGeneratePayoutsCoreAsync()
    {
        var amounts = await LoadPlanAmountsAsync();
        if (amounts.Count == 0) return 0;

        var assigned = await BuildAssignedIncWorkerMapAsync();
        if (assigned.Count == 0) return 0;

        var alreadyDone = (await _uow.Commissions.Query().Select(c => c.SolarRequestId).ToListAsync())
                          .ToHashSet();

        var pending = (await _uow.SolarRequests.GetAllAsync())
                      .Where(r => r.SolarProjectId.HasValue
                               && assigned.ContainsKey(r.Id)
                               && !alreadyDone.Contains(r.Id))
                      .ToList();

        var created = 0;
        foreach (var r in pending)
        {
            if (!amounts.TryGetValue(r.SolarProjectId!.Value, out var amt)) continue;

            await _uow.Commissions.AddAsync(new Commission
            {
                SolarRequestId       = r.Id,
                UserId               = r.UserId,
                WorkerId             = assigned[r.Id],
                ProjectAmount        = r.PlanAmount,
                // Commission is a flat rupee amount now — there is no rate to
                // record. The column stays 0 rather than being back-computed,
                // so nothing downstream mistakes it for a real percentage.
                CommissionPercentage = 0m,
                CommissionAmount     = amt,
                IsPaid               = false,
                Notes                = "Auto-generated from the plan's commission amount."
            });
            created++;
        }

        if (created > 0) await _uow.SaveChangesAsync();
        return created;
    }

    /// <summary>Assigned + uncommissioned projects whose plan has no amount set (or no plan at all).</summary>
    private async Task<int> CountWaitingForRateAsync()
    {
        var amounts = await LoadPlanAmountsAsync();
        var assigned = await BuildAssignedIncWorkerMapAsync();
        if (assigned.Count == 0) return 0;

        var alreadyDone = (await _uow.Commissions.Query().Select(c => c.SolarRequestId).ToListAsync())
                          .ToHashSet();

        return (await _uow.SolarRequests.GetAllAsync())
               .Count(r => assigned.ContainsKey(r.Id)
                        && !alreadyDone.Contains(r.Id)
                        && (!r.SolarProjectId.HasValue || !amounts.ContainsKey(r.SolarProjectId.Value)));
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

            // Payouts are generated as soon as a worker is assigned, but money
            // must not move before the project is actually finished.
            var req = await _uow.SolarRequests.GetByIdAsync(c.SolarRequestId);
            if (req != null && req.CurrentStage != ProjectStatus.Completed)
            {
                return Json(new
                {
                    success = false,
                    message = $"{req.RequestNumber} is at {req.CurrentStage} stage. " +
                              "A commission can only be paid once the project is Completed."
                });
            }

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
