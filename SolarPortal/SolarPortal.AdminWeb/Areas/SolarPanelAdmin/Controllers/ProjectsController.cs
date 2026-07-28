using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using SolarPortal.Application.DTOs;
using SolarPortal.Application.Interfaces;
using SolarPortal.Application.Interfaces.Services;
using SolarPortal.Domain.Entities;

namespace SolarPortal.AdminWeb.Areas.SolarPanelAdmin.Controllers;

[Area("SolarPanelAdmin")]
[Authorize(Roles = "Admin")]
public class ProjectsController : Controller
{
    private readonly ISolarRequestService _solarRequestService;
    private readonly IPaymentService _paymentService;
    private readonly IUnitOfWork _unitOfWork;
    private readonly UserManager<ApplicationUser> _userManager;

    public ProjectsController(
        ISolarRequestService solarRequestService,
        IPaymentService paymentService,
        IUnitOfWork unitOfWork,
        UserManager<ApplicationUser> userManager)
    {
        _solarRequestService = solarRequestService;
        _paymentService = paymentService;
        _unitOfWork = unitOfWork;
        _userManager = userManager;
    }

    public async Task<IActionResult> Index()
    {
        var result = await _solarRequestService.GetAllAsync();
        var projects = (result.Data ?? Enumerable.Empty<SolarRequestDto>()).ToList();

        // Compute per-project payment totals so the admin grid can show
        // Total Amount / Paid / Due columns.
        // Sequential awaits — EF Core forbids concurrent ops on the same DbContext.
        var paidMap = new Dictionary<int, decimal>();
        foreach (var p in projects)
        {
            paidMap[p.Id] = await _paymentService.GetTotalPaidAsync(p.Id);
        }
        ViewBag.PaidMap = paidMap;
        return View(projects);
    }

    public async Task<IActionResult> Details(int id)
    {
        var result = await _solarRequestService.GetWithDetailsAsync(id);
        if (!result.IsSuccess) return NotFound();

        // Per spec: agar yeh sirf auto-stub hai (user ne abhi kuch submit nahi
        // kiya), to admin ko bhi Details + Approve/Reject mat dikhao — All
        // Projects list pe wapas bhej do.
        var d = result.Data;
        if (d != null &&
            !d.SolarProjectId.HasValue &&
            !d.ExternalProductId.HasValue &&
            d.KVCapacity == 0m &&
            d.PlanAmount == 0m &&
            d.RequestedAmount == 0m &&
            d.CurrentStage != SolarPortal.Domain.Enums.ProjectStatus.Completed)
        {
            TempData["Info"] = "This request hasn't been submitted by the user yet.";
            return RedirectToAction(nameof(Index));
        }

        await LoadLifecycleAsync(id, d!);
        return View(result.Data);
    }

    // Details page ab poora project lifecycle dikhata hai — plan/product, payments,
    // aur har workflow stage ka record (meter dispatch, site survey, material
    // dispatch + assigned person, installation + installer, DCR, commission).
    // Pehle sirf request-level fields dikhte the, isliye admin ko project ki poori
    // kahani dekhne ke liye alag-alag workflow pages kholne padte the.
    private async Task LoadLifecycleAsync(int id, SolarRequestDto dto)
    {
        var payments = (await _unitOfWork.Payments.FindAsync(x => x.SolarRequestId == id))
                       .OrderByDescending(x => x.PaymentDate)
                       .ToList();
        ViewBag.Payments = payments;
        // Count yahan se jaata hai kyunki view list ko NON-generic IEnumerable
        // ke roop mein padhta hai (Razor ka runtime compiler `List<Payment>` ke
        // `<Payment>` ko HTML tag samajh leta hai aur page 500 de deta hai).
        ViewBag.PaymentCount = payments.Count;
        ViewBag.TotalPaid = payments.Where(x => x.IsVerified).Sum(x => x.Amount);

        // Har stage ka LATEST record — re-submission par purani rows bhi rehti hain.
        ViewBag.MeterDispatch = (await _unitOfWork.MeterDispatches.FindAsync(x => x.SolarRequestId == id))
                                .OrderByDescending(x => x.CreatedAt).FirstOrDefault();
        ViewBag.SiteSurvey = (await _unitOfWork.SiteSurveys.FindAsync(x => x.SolarRequestId == id))
                             .OrderByDescending(x => x.CreatedAt).FirstOrDefault();

        var material = (await _unitOfWork.MaterialDispatches.FindAsync(x => x.SolarRequestId == id))
                       .OrderByDescending(x => x.CreatedAt).FirstOrDefault();
        var installation = (await _unitOfWork.Installations.FindAsync(x => x.SolarRequestId == id))
                           .OrderByDescending(x => x.CreatedAt).FirstOrDefault();
        ViewBag.MaterialDispatch = material;
        ViewBag.Installation = installation;

        ViewBag.DCR = (await _unitOfWork.DCRDocuments.FindAsync(x => x.SolarRequestId == id))
                      .OrderByDescending(x => x.CreatedAt).FirstOrDefault();
        ViewBag.Commission = (await _unitOfWork.Commissions.FindAsync(x => x.SolarRequestId == id))
                             .OrderByDescending(x => x.CreatedAt).FirstOrDefault();

        // FindAsync navigation properties eager-load nahi karta, isliye worker ke
        // naam alag se laate hain (warna "Assigned person" blank dikhta).
        var workerIds = new[] { material?.AssignedWorkerId, installation?.AssignedWorkerId }
                        .Where(x => x.HasValue).Select(x => x!.Value).Distinct().ToList();
        var workerNames = workerIds.Count == 0
            ? new Dictionary<int, string>()
            : (await _unitOfWork.Workers.FindAsync(w => workerIds.Contains(w.Id)))
              .ToDictionary(w => w.Id, w => w.Name);

        // View ko seedhe do naam bhejte hain — wahan dictionary rakhne ka matlab
        // generic type argument likhna hota, jo upar wali hi dikkat paida karta.
        string? NameOf(int? wid) => wid.HasValue && workerNames.TryGetValue(wid.Value, out var n) ? n : null;
        ViewBag.MaterialWorkerName = NameOf(material?.AssignedWorkerId);
        ViewBag.InstallerName = NameOf(installation?.AssignedWorkerId);

        // Plan master (SolarProjects) — sirf tab jab request usse bandhi ho.
        ViewBag.SolarProject = dto.SolarProjectId.HasValue
            ? await _unitOfWork.SolarProjects.GetByIdAsync(dto.SolarProjectId.Value)
            : null;
    }

    public async Task<IActionResult> Approvals()
    {
        var result = await _solarRequestService.GetPendingApprovalsAsync();
        var pending = (result.Data ?? Enumerable.Empty<SolarRequestDto>()).ToList();

        // Payments per request (so the approval card can show what the user has paid so far).
        // EF Core requires sequential awaits on the same DbContext.
        var paidMap = new Dictionary<int, decimal>();
        var paymentsMap = new Dictionary<int, List<SolarPortal.Domain.Entities.Payment>>();
        var allPayments = await _unitOfWork.Payments.GetAllAsync();
        foreach (var p in pending)
        {
            var related = allPayments.Where(x => x.SolarRequestId == p.Id)
                                     .OrderByDescending(x => x.CreatedAt).ToList();
            paymentsMap[p.Id] = related;
            paidMap[p.Id] = related.Where(x => x.IsVerified).Sum(x => x.Amount);
        }
        ViewBag.PaidMap = paidMap;
        ViewBag.PaymentsMap = paymentsMap;
        return View(pending);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Approve(int id, string? notes)
    {
        var adminId = _userManager.GetUserId(User)!;
        var result = await _solarRequestService.ApproveAsync(id, adminId, notes);
        return Json(new { success = result.IsSuccess, message = result.Message ?? result.Errors.FirstOrDefault() });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Reject(int id, string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return Json(new { success = false, message = "Rejection reason is required." });
        var adminId = _userManager.GetUserId(User)!;
        var result = await _solarRequestService.RejectAsync(id, adminId, reason);
        return Json(new { success = result.IsSuccess, message = result.Message ?? result.Errors.FirstOrDefault() });
    }

    [HttpPost]
    public async Task<IActionResult> UpdateStage(UpdateSolarRequestStatusDto dto)
    {
        var adminId = _userManager.GetUserId(User)!;
        var result = await _solarRequestService.UpdateStageAsync(dto, adminId);
        return Json(new { success = result.IsSuccess, message = result.Message ?? result.Errors.FirstOrDefault() });
    }

    // POST: /SolarPanelAdmin/Projects/UpdateAmount
    // Admin can override project total amount until the project is Completed.
    // Per spec: "Admin can edit any project/request details anytime until project completion."
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateAmount(int id, decimal amount, string? reason)
    {
        try
        {
            if (amount <= 0)
                return Json(new { success = false, message = "Amount must be greater than zero." });

            var req = await _unitOfWork.SolarRequests.GetByIdAsync(id);
            if (req == null)
                return Json(new { success = false, message = "Request not found." });

            if (req.CurrentStage == Domain.Enums.ProjectStatus.Completed)
                return Json(new { success = false, message = "Cannot edit a completed project." });

            var oldAmount = req.PlanAmount;
            req.PlanAmount = amount;
            req.UpdatedAt = DateTime.UtcNow;
            _unitOfWork.SolarRequests.Update(req);
            await _unitOfWork.SaveChangesAsync();

            return Json(new
            {
                success = true,
                message = $"Project amount updated from ₹{oldAmount:N0} to ₹{amount:N0}." +
                          (string.IsNullOrWhiteSpace(reason) ? "" : $" Reason: {reason}")
            });
        }
        catch (Exception ex)
        {
            var detail = ex.InnerException?.Message ?? ex.Message;
            return Json(new { success = false, message = $"Update failed: {detail}" });
        }
    }
}
