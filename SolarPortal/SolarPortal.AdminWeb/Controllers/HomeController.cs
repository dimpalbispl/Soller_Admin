using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using SolarPortal.AdminWeb.Models;

namespace SolarPortal.AdminWeb.Controllers;

public class HomeController : Controller
{
    private readonly ILogger<HomeController> _logger;

    public HomeController(ILogger<HomeController> logger)
    {
        _logger = logger;
    }

    // "/" must land on the admin dashboard, not the scaffolded welcome view.
    // There is no public landing page in this app, so a signed-out visitor goes
    // to login and comes back here afterwards.
    public IActionResult Index()
    {
        if (User.Identity?.IsAuthenticated != true)
            return RedirectToAction("Login", "Account");

        return RedirectToAction("Index", "Dashboard", new { area = "SolarPanelAdmin" });
    }

    public IActionResult Privacy()
    {
        return View();
    }

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
    }
}
