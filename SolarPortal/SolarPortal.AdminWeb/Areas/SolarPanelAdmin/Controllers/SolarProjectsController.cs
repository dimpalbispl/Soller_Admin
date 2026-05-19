using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SolarPortal.Application.DTOs;
using SolarPortal.Application.Interfaces.Services;

namespace SolarPortal.AdminWeb.Areas.SolarPanelAdmin.Controllers;

[Area("SolarPanelAdmin")]
[Authorize(Roles = "Admin")]
public class SolarProjectsController : Controller
{
    private readonly ISolarProjectService _service;

    public SolarProjectsController(ISolarProjectService service) => _service = service;

    public async Task<IActionResult> Index()
    {
        var items = await _service.GetAllAsync(activeOnly: false);
        return View(items);
    }

    [HttpGet]
    public IActionResult Create() => View(new CreateSolarProjectDto());

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(CreateSolarProjectDto dto)
    {
        if (!ModelState.IsValid) return View(dto);

        var result = await _service.CreateAsync(dto);
        if (!result.IsSuccess)
        {
            foreach (var e in result.Errors) ModelState.AddModelError(string.Empty, e);
            return View(dto);
        }

        TempData["Success"] = "Solar project created";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    public async Task<IActionResult> ToggleActive(int id)
    {
        var result = await _service.ToggleActiveAsync(id);
        return Json(new { success = result.IsSuccess, message = result.Message });
    }

    [HttpPost]
    public async Task<IActionResult> Delete(int id)
    {
        var result = await _service.DeleteAsync(id);
        return Json(new { success = result.IsSuccess, message = result.Message });
    }
}
