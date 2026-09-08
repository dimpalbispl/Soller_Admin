using SolarPortal.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using SolarPortal.Application.Interfaces;
using SolarPortal.Application.Interfaces.Services;
using SolarPortal.AdminWeb.Areas.SolarPanelAdmin.Helpers;
using SolarPortal.Domain.Entities;
using SolarPortal.Domain.Enums;

namespace SolarPortal.AdminWeb.Areas.SolarPanelAdmin.Controllers;

/// <summary>
/// Change request point 7 — "Admin me Add Fund → 2 step. Alag 2 menu:
/// (1) Add Fund (2) Approve Fund."
///
/// Both menus have since been folded into Payment Verification: first Add Fund
/// ("add fund ka option alag na dekar payment approve me hi de do"), and now
/// Approve Fund too ("add fund ki request Payment Verification me hi jayegi, wahin
/// se verify ya reject karenge to fund mil jayega"). This controller's Index page
/// is kept (unlinked, reachable by direct URL) and Approve now just redirects.
///
/// Add Fund still records the money as UNVERIFIED — it is an ordinary pending
/// Payment row, so it lands in the Payment Verification queue and does not count
/// toward the project total (stage gates, dues, reports) until it is verified
/// there. Because that one screen now both adds and verifies, the old
/// maker-checker rule (a different admin had to approve) no longer applies.
/// </summary>
[Area("SolarPanelAdmin")]
[Authorize(Roles = "Admin,SuperAdmin")]
public class FundsController : Controller
{
    private readonly IAdminFundService _funds;
    private readonly IUnitOfWork _uow;
    private readonly ApplicationDbContext _db;
    private readonly IFileUploadService _fileUploadService;
    private readonly IPaymentService _payments;
    private readonly IAdminActivityLogger _activity;
    private readonly UserManager<ApplicationUser> _userManager;

    public FundsController(
        IAdminFundService funds,
        IUnitOfWork uow,
        ApplicationDbContext db,
        IFileUploadService fileUploadService,
        IPaymentService payments,
        IAdminActivityLogger activity,
        UserManager<ApplicationUser> userManager)
    {
        _funds = funds;
        _uow = uow;
        _db = db;
        _fileUploadService = fileUploadService;
        _payments = payments;
        _activity = activity;
        _userManager = userManager;
    }

    private string AdminId => _userManager.GetUserId(User) ?? "system";
    private string? ClientIp => HttpContext.Connection.RemoteIpAddress?.ToString();

    private async Task<string> AdminNameAsync()
    {
        var me = await _userManager.GetUserAsync(User);
        return me?.FullName ?? me?.UserName ?? AdminId;
    }

    /// <summary>Rows per page of the fund history, so this page never grows endlessly.</summary>
    private const int HistoryPageSize = 10;

    // ── Menu 1: Add Fund ──────────────────────────────────────────────────
    // GET: /SolarPanelAdmin/Funds?filter=all|pending|verified|rejected&page=1
    public async Task<IActionResult> Index(string? filter, int page = 1)
    {
        // No project list is loaded any more. The admin types a Member ID and
        // Lookup below resolves it - which is what an admin actually has to hand,
        // and one query instead of one-per-project on every page load.
        var f = (filter ?? "all").ToLowerInvariant();

        // "Yahan se kab kab fund transfer hua" - the full trail of every fund added
        // from this screen (and from the Add Fund modal on Payment Verification, the
        // same entry point), newest first. It used to list only the current admin's
        // still-pending entries, so a fund disappeared from view the moment it was
        // decided and there was nowhere to see what had been transferred.
        var all = (await _funds.GetPendingAsync())
                  .Concat(await _funds.GetDecidedAsync())
                  .ToList();

        ViewBag.CountAll      = all.Count;
        ViewBag.CountPending  = all.Count(p => !p.IsVerified && p.Status != PaymentStatus.Rejected);
        ViewBag.CountVerified = all.Count(p => p.IsVerified);
        ViewBag.CountRejected = all.Count(p => p.Status == PaymentStatus.Rejected);

        var rows = f switch
        {
            "pending"  => all.Where(p => !p.IsVerified && p.Status != PaymentStatus.Rejected),
            "verified" => all.Where(p => p.IsVerified),
            "rejected" => all.Where(p => p.Status == PaymentStatus.Rejected),
            _          => all
        };

        var ordered = rows.OrderByDescending(p => p.CreatedAt)
                          .ThenByDescending(p => p.Id)
                          .ToList();

        var totalPages = Math.Max(1, (int)Math.Ceiling(ordered.Count / (double)HistoryPageSize));
        page = Math.Clamp(page, 1, totalPages);
        var pageRows = ordered.Skip((page - 1) * HistoryPageSize).Take(HistoryPageSize).ToList();

        // Only the rows actually on screen need their request hydrated.
        var reqIds = pageRows.Select(r => r.SolarRequestId).Distinct().ToHashSet();
        ViewBag.Requests = (await _uow.SolarRequests.GetAllAsync())
                           .Where(r => reqIds.Contains(r.Id))
                           .ToDictionary(r => r.Id);

        ViewBag.History    = pageRows;
        ViewBag.Filter     = f;
        ViewBag.Page       = page;
        ViewBag.TotalPages = totalPages;
        ViewBag.TotalCount = ordered.Count;
        ViewBag.PageSize   = HistoryPageSize;
        ViewBag.MyId       = AdminId;
        ViewBag.Title = "Add Fund";
        return View();
    }

    // GET: /SolarPanelAdmin/Funds/Lookup?memberId=SADHNATEST05
    //
    // Resolves a Member ID to the member and their live project(s). The Payment
    // Verification page asks the same question through its own Lookup action, so
    // the answer is built in one shared place.
    [HttpGet]
    public async Task<IActionResult> Lookup(string? memberId) =>
        Json(await MemberFundLookup.ResolveAsync(memberId, _db, _uow, _payments));

    // GET: /SolarPanelAdmin/Funds/CheckUtr?solarRequestId=9&utr=1
    //
    // Live duplicate check for the UTR box — the clash used to surface only after
    // the admin filled the whole form and pressed save. Same rule the save itself
    // applies, so the field can never say "fine" on something Add would refuse.
    [HttpGet]
    public async Task<IActionResult> CheckUtr(int solarRequestId, string? utr)
    {
        var duplicate = await _funds.IsDuplicateUtrAsync(solarRequestId, utr);
        return Json(new
        {
            duplicate,
            message = duplicate
                ? $"A payment with UTR {utr?.Trim()} already exists on this request."
                : ""
        });
    }

    // POST: /SolarPanelAdmin/Funds/Add
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Add(int solarRequestId, decimal amount, string utrNumber,
        DateTime? paymentDate, string? referenceNumber, string? notes, IFormFile? receiptImage)
    {
        string? receiptPath = null;
        if (receiptImage != null && receiptImage.Length > 0)
        {
            var (ok, path, err) = await _fileUploadService.UploadAsync(receiptImage, "payments");
            if (!ok) return Json(new { success = false, message = $"Receipt upload failed: {err}" });
            receiptPath = path;
        }

        var result = await _funds.AddAsync(new AddFundInput
        {
            SolarRequestId = solarRequestId,
            Amount = amount,
            UtrNumber = utrNumber,
            PaymentDate = paymentDate,
            ReferenceNumber = referenceNumber,
            Notes = notes,
            ReceiptPath = receiptPath,
            AdminId = AdminId,
            AdminName = await AdminNameAsync()
        });

        if (!result.IsSuccess)
            return Json(new { success = false, message = result.Message ?? result.Errors.FirstOrDefault() });

        await _activity.LogAsync(AdminId, "Fund.Add", "Payment", result.Data!.Id.ToString(),
            $"Added fund ₹{amount:N0} (UTR {utrNumber}) on request #{solarRequestId}. Awaiting approval.", ClientIp);

        return Json(new { success = true, message = result.Message });
    }

    // ── Approve Fund: retired ─────────────────────────────────────────────
    // The separate Approve Fund report is gone - "add fund ki request lagayenge aur
    // Payment Verification me hi jayegi, wahin se verify ya reject karenge to fund
    // mil jayega". An added fund is an unverified Payment row, so it already shows
    // up as Pending on Payment Verification and is released by Verify there.
    //
    // The old URL is kept and simply redirects, so a bookmark lands on the one screen
    // that now decides funds instead of a second, competing queue.
    public IActionResult Approve()
    {
        TempData["Info"] = "Approve Fund has moved — added funds now wait here in Payment Verification. "
                         + "Verify one to credit it, or reject it.";
        return RedirectToAction("Index", "Payments", new { filter = "pending" });
    }

    // POST: /SolarPanelAdmin/Funds/ApproveEntry
    // No screen posts here any more (the Approve Fund page it belonged to is gone) —
    // Payment Verification's own Verify/Reject decides these rows now. Left in place
    // so the maker-checker service path is still callable if that queue is ever
    // brought back.
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ApproveEntry(int id, string? note)
    {
        var result = await _funds.ApproveAsync(id, AdminId, note);
        if (result.IsSuccess)
        {
            await _activity.LogAsync(AdminId, "Fund.Approve", "Payment", id.ToString(),
                result.Message, ClientIp);
        }
        return Json(new { success = result.IsSuccess, message = result.Message ?? result.Errors.FirstOrDefault() });
    }

    // POST: /SolarPanelAdmin/Funds/RejectEntry
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RejectEntry(int id, string reason)
    {
        var result = await _funds.RejectAsync(id, AdminId, reason);
        if (result.IsSuccess)
        {
            await _activity.LogAsync(AdminId, "Fund.Reject", "Payment", id.ToString(),
                $"Rejected admin fund #{id}. Reason: {reason}", ClientIp);
        }
        return Json(new { success = result.IsSuccess, message = result.Message ?? result.Errors.FirstOrDefault() });
    }
}
