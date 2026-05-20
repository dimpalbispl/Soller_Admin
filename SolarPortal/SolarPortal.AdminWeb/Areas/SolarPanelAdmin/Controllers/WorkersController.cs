using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SolarPortal.Application.DTOs;
using SolarPortal.Application.Interfaces.Services;

namespace SolarPortal.AdminWeb.Areas.SolarPanelAdmin.Controllers;

[Area("SolarPanelAdmin")]
[Authorize(Roles = "Admin")]
public class WorkersController : Controller
{
    private readonly IWorkerService _workerService;

    public WorkersController(IWorkerService workerService) => _workerService = workerService;

    public async Task<IActionResult> Index()
    {
        var workers = await _workerService.GetAllAsync();
        return View(workers);
    }

    [HttpGet]
    public IActionResult Create() => View();

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

        if (!ModelState.IsValid) return View(dto);
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