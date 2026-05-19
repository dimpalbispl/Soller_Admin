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
        return View(result.Data);
    }

    public async Task<IActionResult> Approvals()
    {
        var result = await _solarRequestService.GetPendingApprovalsAsync();
        return View(result.Data ?? Enumerable.Empty<SolarRequestDto>());
    }

    [HttpPost]
    public async Task<IActionResult> Approve(int id, string? notes)
    {
        var adminId = _userManager.GetUserId(User)!;
        var result = await _solarRequestService.ApproveAsync(id, adminId, notes);
        return Json(new { success = result.IsSuccess, message = result.Message ?? result.Errors.FirstOrDefault() });
    }

    [HttpPost]
    public async Task<IActionResult> Reject(int id, string reason)
    {
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