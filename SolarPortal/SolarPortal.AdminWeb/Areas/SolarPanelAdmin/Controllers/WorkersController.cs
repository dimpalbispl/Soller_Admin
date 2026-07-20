using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SolarPortal.Application.DTOs;
using SolarPortal.Application.Interfaces.Services;
using SolarPortal.Application.Services;

namespace SolarPortal.AdminWeb.Areas.SolarPanelAdmin.Controllers;

[Area("SolarPanelAdmin")]
[Authorize(Roles = "Admin")]
public class WorkersController : Controller
{
    private readonly IWorkerService _workerService;
    private readonly IStateService _states;

    public WorkersController(IWorkerService workerService, IStateService states)
    {
        _workerService = workerService;
        _states = states;
    }

    public async Task<IActionResult> Index()
    {
        var workers = await _workerService.GetAllAsync();
        return View(workers);
    }

    [HttpGet]
    public async Task<IActionResult> Create()
    {
        // States from legacy M_StateDivMaster — same source as the user panel.
        ViewBag.States = await _states.GetActiveAsync();
        return View();
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(CreateWorkerDto dto)
    {
        // Specialization & IsAvailable were removed from the form per spec.
        // Default them here so ModelState validation passes and DB defaults are sensible.
        if (string.IsNullOrWhiteSpace(dto.Specialization))
            dto.Specialization = "General";
        // dto.IsAvailable is already `true` by default in the DTO.
        ModelState.Remove(nameof(dto.Specialization));

        // Login credentials apply to BOTH worker types — JOB and INC workers
        // each get a panel login. Commission stays INC-only: JOB is salaried.
        if (dto.Type != SolarPortal.Domain.Enums.WorkerType.INC)
        {
            dto.CommissionPercent = null;
        }

        dto.LoginUsername = dto.LoginUsername?.Trim();

        // Username is the login key, so it must be unique across all workers.
        if (!string.IsNullOrWhiteSpace(dto.LoginUsername)
            && await _workerService.LoginUsernameExistsAsync(dto.LoginUsername))
        {
            ModelState.AddModelError(nameof(dto.LoginUsername),
                "This username is already taken by another worker.");
        }

        if (!ModelState.IsValid)
        {
            // Re-populate states so the dropdown isn't empty on validation re-render.
            ViewBag.States = await _states.GetActiveAsync();
            return View(dto);
        }
        await _workerService.CreateAsync(dto);
        TempData["Success"] = "Member added successfully";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    public async Task<IActionResult> Delete(int id)
    {
        await _workerService.DeleteAsync(id);
        return Json(new { success = true });
    }

    [HttpPost]
    public async Task<IActionResult> ToggleAvailability(int id)
    {
        await _workerService.ToggleAvailabilityAsync(id);
        return Json(new { success = true });
    }

    [HttpPost]
    public async Task<IActionResult> AssignToInstallation(int installationId, int workerId)
    {
        var result = await _workerService.AssignToInstallationAsync(installationId, workerId);
        return Json(new { success = result });
    }
}