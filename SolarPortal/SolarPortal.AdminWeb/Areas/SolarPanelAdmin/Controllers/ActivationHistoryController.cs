using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SolarPortal.Application.DTOs;
using SolarPortal.Application.Services;

namespace SolarPortal.AdminWeb.Areas.SolarPanelAdmin.Controllers;

/// <summary>
/// Activation History — a report of its own, separate from All Projects and
/// Payment Verification, per spec (Hinglish):
///
///   "without activation liya fir payment complete karke apni id active ki,
///    ab Active Now se project le raha hu — user ko pata rehna chahiye ki
///    pehle kya liya, fir kya liya, kab liya. Uski alag report bana do."
///
/// One row per member with the three mode-first-use dates and the activation
/// date side by side, plus an expandable timeline of every dated event on that
/// member's journey.
/// </summary>
[Area("SolarPanelAdmin")]
[Authorize(Roles = "Admin,SuperAdmin")]
public class ActivationHistoryController : Controller
{
    private readonly IActivationHistoryService _history;

    public ActivationHistoryController(IActivationHistoryService history)
    {
        _history = history;
    }

    // GET: /SolarPanelAdmin/ActivationHistory
    public async Task<IActionResult> Index(string? filter)
    {
        var all = await _history.GetAllAsync();
        var f = (filter ?? "all").ToLowerInvariant();

        // Tab counts always come off the UNFILTERED set so the numbers don't
        // change as you click between tabs.
        ViewBag.CountAll = all.Count;
        ViewBag.CountJourney = all.Count(m => m.ActivatedLater);
        ViewBag.CountActive = all.Count(m => m.IsActiveNow);
        ViewBag.CountInactive = all.Count(m => !m.IsActiveNow);

        ViewBag.Filter = f;
        return View(Apply(all, f));
    }

    // GET: /SolarPanelAdmin/ActivationHistory/Export
    // Flat CSV — one line per timeline event, so the whole journey can be
    // opened in Excel or handed to accounts.
    public async Task<IActionResult> Export(string? filter)
    {
        var rows = Apply(await _history.GetAllAsync(), (filter ?? "all").ToLowerInvariant());

        var sb = new StringBuilder();
        sb.AppendLine("Member ID,Member Name,Mobile,City,State,Currently Active,Activated On,Activation Date Exact,Event Date,Event,Detail,Amount,Reference");

        foreach (var m in rows)
        {
            foreach (var e in m.Events)
            {
                sb.AppendLine(string.Join(",", new[]
                {
                    Csv(m.MemberIdNo),
                    Csv(m.MemberName),
                    Csv(m.Mobile),
                    Csv(m.City),
                    Csv(m.State),
                    Csv(m.IsActiveNow ? "Yes" : "No"),
                    Csv(m.ActivatedOn?.ToString("dd MMM yyyy HH:mm")),
                    Csv(!m.ActivatedOn.HasValue ? "" : (m.ActivatedOnApproximate ? "Inferred" : "Exact")),
                    Csv(e.When.ToString("dd MMM yyyy HH:mm")),
                    Csv(e.Title),
                    Csv(e.Detail),
                    Csv(e.Amount?.ToString("0.##")),
                    Csv(e.Reference)
                }));
            }
        }

        // UTF-8 BOM so Excel picks up the rupee sign and member names correctly.
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
        return File(bytes, "text/csv", $"activation-history-{DateTime.Now:yyyyMMdd-HHmm}.csv");
    }

    private static List<MemberActivationHistoryDto> Apply(
        IReadOnlyList<MemberActivationHistoryDto> all, string filter) => filter switch
        {
            // The cohort this report was asked for: started on a non-activation
            // mode and only became active later.
            "journey" => all.Where(m => m.ActivatedLater).ToList(),
            "active" => all.Where(m => m.IsActiveNow).ToList(),
            "inactive" => all.Where(m => !m.IsActiveNow).ToList(),
            _ => all.ToList()
        };

    private static string Csv(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var needsQuotes = value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0;
        var escaped = value.Replace("\"", "\"\"");
        return needsQuotes ? $"\"{escaped}\"" : escaped;
    }
}
