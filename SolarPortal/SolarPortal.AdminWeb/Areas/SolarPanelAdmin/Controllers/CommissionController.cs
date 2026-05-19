using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SolarPortal.Application.Interfaces;

namespace SolarPortal.AdminWeb.Areas.SolarPanelAdmin.Controllers;

[Area("SolarPanelAdmin")]
[Authorize(Roles = "Admin")]
public class CommissionController : Controller
{
    private readonly IUnitOfWork _uow;

    public CommissionController(IUnitOfWork uow) => _uow = uow;

    public async Task<IActionResult> Index()
    {
        var commissions = await _uow.Commissions.Query()
            .OrderByDescending(c => c.CreatedAt)
            .ToListAsync();
        return View(commissions);
    }
}
