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
        if (!ModelState.IsValid) return View(dto);
        await _workerService.CreateAsync(dto);
        TempData["Success"] = "Worker added successfully";
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