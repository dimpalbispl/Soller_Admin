using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SolarPortal.Application.Interfaces.Services;

namespace SolarPortal.AdminWeb.Areas.SolarPanelAdmin.Controllers;

/// <summary>
/// Wallet Transaction report — every credit and debit that has touched a wallet,
/// read straight from the ledger (IncTrnvoucher). Whatever wrote the row shows up
/// here: INC commission, an INC withdrawal, or an Extra Payment Refund / debit.
///
/// Read-only by design. A wallet balance is corrected by posting an opposing
/// entry through the flow that owns it, never by editing a ledger row.
/// </summary>
[Area("SolarPanelAdmin")]
[Authorize(Roles = "Admin,SuperAdmin")]
public class WalletReportController : Controller
{
    private readonly IWalletLedgerReportService _report;

    /// <summary>Row cap, so an open-ended date range can never load the whole ledger.</summary>
    private const int MaxRows = 1000;

    public WalletReportController(IWalletLedgerReportService report) => _report = report;

    // GET: /SolarPanelAdmin/WalletReport?from=&to=&wallet=I&memberId=TESTSUNIL
    //
    // Date / wallet / member filters run in the QUERY (not just on the rows already
    // on screen), so they can reach past the row cap. The toolbar on the page adds
    // free-text search over the remark on top of whatever came back.
    public async Task<IActionResult> Index(DateTime? from, DateTime? to, string? wallet, string? memberId)
    {
        var rows = await _report.GetAsync(from, to, wallet, memberId, MaxRows);

        ViewBag.From      = from;
        ViewBag.To        = to;
        ViewBag.Wallet    = wallet;
        ViewBag.MemberId  = memberId;
        ViewBag.Wallets   = await _report.GetWalletsAsync();
        ViewBag.MaxRows   = MaxRows;
        // Say so out loud when the cap bit, rather than showing a truncated list
        // that reads like everything there is.
        ViewBag.Truncated = rows.Count >= MaxRows;

        ViewBag.TotalCredit = rows.Where(r => r.IsCredit).Sum(r => r.Amount);
        ViewBag.TotalDebit  = rows.Where(r => !r.IsCredit).Sum(r => r.Amount);

        return View(rows);
    }

    // GET: /SolarPanelAdmin/WalletReport/Export — same filters, as CSV for Excel.
    public async Task<IActionResult> Export(DateTime? from, DateTime? to, string? wallet, string? memberId)
    {
        var rows = await _report.GetAsync(from, to, wallet, memberId, MaxRows);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Date,Posted At,Voucher No,Wallet,Account,Name,Type,Credit,Debit,Reference,Remark");
        foreach (var r in rows)
        {
            sb.AppendLine(string.Join(",", new[]
            {
                Csv(r.VoucherDate?.ToString("dd MMM yyyy")),
                Csv(r.PostedAt?.ToLocalTime().ToString("dd MMM yyyy HH:mm")),
                Csv(r.VoucherNo),
                Csv(r.WalletName ?? r.AcType),
                Csv(r.AccountId),
                Csv(r.AccountName),
                Csv(r.IsCredit ? "Credit" : "Debit"),
                Csv(r.IsCredit ? r.Amount.ToString("0.##") : ""),
                Csv(r.IsCredit ? "" : r.Amount.ToString("0.##")),
                Csv(r.RefNo),
                Csv(r.Remark)
            }));
        }

        // BOM so Excel reads the rupee sign and Hindi names correctly.
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }
            .Concat(System.Text.Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
        return File(bytes, "text/csv", $"wallet-transactions-{DateTime.Now:yyyyMMdd-HHmm}.csv");
    }

    /// <summary>One CSV cell: quoted, with embedded quotes doubled.</summary>
    private static string Csv(string? value) =>
        "\"" + (value ?? string.Empty).Replace("\"", "\"\"") + "\"";
}
