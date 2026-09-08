using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SolarPortal.Application.Interfaces.Services;

namespace SolarPortal.AdminWeb.Areas.SolarPanelAdmin.Controllers;

/// <summary>
/// What Remaining BV approval wrote into the legacy MLM ledger, shown with the
/// member and order details attached.
///
/// Read-only by design. Repurchincome is the input to every MLM income
/// calculation; a ledger row is corrected by posting an opposing entry through
/// the flow that owns it, never by editing it from a report screen.
/// </summary>
[Area("SolarPanelAdmin")]
[Authorize(Roles = "Admin")]
public class RemainingBvReportController : Controller
{
    private readonly IRepurchaseIncomeReportService _report;

    /// <summary>Row cap, so an unbounded date range can never load the whole ledger.</summary>
    private const int MaxRows = 1000;

    public RemainingBvReportController(IRepurchaseIncomeReportService report)
    {
        _report = report;
    }

    // GET: /SolarPanelAdmin/RemainingBvReport
    public async Task<IActionResult> Index(DateTime? from, DateTime? to)
    {
        var rows = await _report.GetAsync(from, to, MaxRows);

        ViewBag.From = from;
        ViewBag.To = to;
        ViewBag.MaxRows = MaxRows;
        // The view says so out loud when the cap bit, rather than showing a
        // truncated list that reads like everything there is.
        ViewBag.Truncated = rows.Count >= MaxRows;

        return View(rows);
    }
}
