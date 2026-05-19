using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

namespace SolarPortal.AdminWeb.Areas.SolarPanelAdmin.Controllers;

[Area("SolarPanelAdmin")]
[Authorize(Roles = "Admin")]
public class SettingsController : Controller
{
    private readonly IConfiguration _config;

    public SettingsController(IConfiguration config) => _config = config;

    public IActionResult Index()
    {
        ViewBag.MaxFileSizeMB = _config.GetValue<int>("FileUpload:MaxFileSizeMB", 10);
        ViewBag.UploadPath = _config.GetValue<string>("FileUpload:UploadPath") ?? "wwwroot/uploads";
        return View();
    }
}
