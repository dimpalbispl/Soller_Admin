using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SolarPortal.Application.Interfaces.Services;

namespace SolarPortal.AdminWeb.Areas.SolarPanelAdmin.Controllers;

[Area("SolarPanelAdmin")]
[Authorize(Roles = "Admin")]
public class DashboardController : Controller
{
    private readonly IDashboardService _dashboardService;

    public DashboardController(IDashboardService dashboardService)
        => _dashboardService = dashboardService;

    public async Task<IActionResult> Index()
    {
        var dashboard = await _dashboardService.GetAdminDashboardAsync();
        return View(dashboard);
    }

    [HttpGet]
    public async Task<IActionResult> GetStats()
    {
        var dashboard = await _dashboardService.GetAdminDashboardAsync();
        return Json(new
        {
            totalProjects = dashboard.TotalProjects,
            pendingApprovals = dashboard.PendingApprovals,
            activeInstallations = dashboard.ActiveInstallations,
            completedProjects = dashboard.CompletedProjects,
            totalRevenue = dashboard.TotalRevenue,
            statusDistribution = dashboard.StatusDistribution
        });
    }
}