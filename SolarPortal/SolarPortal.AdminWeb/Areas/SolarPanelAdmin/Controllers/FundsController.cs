using SolarPortal.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using SolarPortal.Application.Interfaces;
using SolarPortal.Application.Interfaces.Services;
using SolarPortal.Domain.Entities;

namespace SolarPortal.AdminWeb.Areas.SolarPanelAdmin.Controllers;

/// <summary>
/// Change request point 7 — "Admin me Add Fund → 2 step. Alag 2 menu:
/// (1) Add Fund (2) Approve Fund."
///
/// Add Fund records the money as UNVERIFIED; Approve Fund is where a second
/// admin confirms it. Until then the amount does not count toward the project
/// total, so nothing downstream (stage gates, dues, reports) moves on an entry
/// that has only been typed in.
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

    // ── Menu 1: Add Fund ──────────────────────────────────────────────────
    // GET: /SolarPanelAdmin/Funds
    public async Task<IActionResult> Index()
    {
        // No project list is loaded any more. The admin types a Member ID and
        // Lookup below resolves it - which is what an admin actually has to hand,
        // and one query instead of one-per-project on every page load.
        ViewBag.MyPending = (await _funds.GetPendingAsync())
                            .Where(p => string.Equals(p.FundAddedBy, AdminId, StringComparison.OrdinalIgnoreCase))
                            .ToList();
        ViewBag.Title = "Add Fund";
        return View();
    }

    // GET: /SolarPanelAdmin/Funds/Lookup?memberId=SADHNATEST05
    //
    // Resolves a Member ID to the member and their live project(s).
    //
    // The member is looked up in m_membermaster FIRST, separately from their solar
    // requests. Checking only SolarRequests reported "No member found" for a real
    // member who simply has not filed a solar request yet - which sent the admin
    // hunting for a typo that was never there.
    [HttpGet]
    public async Task<IActionResult> Lookup(string? memberId)
    {
        var id = (memberId ?? string.Empty).Trim();
        if (id.Length == 0)
            return Json(new { found = false, message = "Enter a Member ID." });

        // Trim BOTH sides: legacy columns are frequently CHAR-padded, and an exact
        // == against a padded value is the classic silent no-match.
        var member = await _db.Members.AsNoTracking()
                             .FirstOrDefaultAsync(m => m.IdNo != null && m.IdNo.Trim() == id);

        var requests = (await _uow.SolarRequests.FindAsync(r => r.UserId != null && r.UserId.Trim() == id))
                       .OrderByDescending(r => r.CreatedAt)
                       .ToList();

        if (member == null && requests.Count == 0)
            return Json(new { found = false, message = $"No member found with ID '{id}'. Check the spelling." });

        var live = requests.Where(r => r.CurrentStage != Domain.Enums.ProjectStatus.Completed).ToList();

        if (live.Count == 0)
        {
            // Three different dead ends, three different actions for the admin.
            var who = member != null ? $" ({member.FullName})" : "";
            var msg = requests.Count == 0
                ? $"Member {id}{who} exists but has no solar request yet — a fund needs a request to attach to."
                : $"Member {id}{who} has no live project — every request is already completed.";
            return Json(new { found = false, message = msg });
        }

        var rows = new List<object>();
        foreach (var r in live)
        {
            var paid = await _payments.GetVerifiedPaidAsync(r.Id);
            rows.Add(new
            {
                id = r.Id,
                requestNumber = r.RequestNumber,
                plan = r.SelectedPlan,
                total = r.PlanAmount,
                paid,
                due = Math.Max(0m, r.PlanAmount - paid),
                stage = r.CurrentStage.ToString()
            });
        }

        var first = live[0];
        var name = member?.FullName;
        if (string.IsNullOrWhiteSpace(name))
            name = string.IsNullOrWhiteSpace(first.MemberFullName) ? first.ApplicantName : first.MemberFullName;

        return Json(new
        {
            found = true,
            memberId = id,
            name,
            mobile = member?.Mobl?.ToString("0") ?? first.MobileNumber,
            city = member?.City ?? first.City,
            requests = rows
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

    // ── Menu 2: Approve Fund ──────────────────────────────────────────────
    // GET: /SolarPanelAdmin/Funds/Approve?filter=pending|decided|all
    public async Task<IActionResult> Approve(string? filter)
    {
        var f = (filter ?? "pending").ToLowerInvariant();

        var rows = f switch
        {
            "decided" => await _funds.GetDecidedAsync(),
            "all" => (await _funds.GetPendingAsync()).Concat(await _funds.GetDecidedAsync())
                     .OrderByDescending(p => p.CreatedAt).ToList(),
            _ => await _funds.GetPendingAsync()
        };

        var reqIds = rows.Select(r => r.SolarRequestId).Distinct().ToHashSet();
        ViewBag.Requests = (await _uow.SolarRequests.GetAllAsync())
                           .Where(r => reqIds.Contains(r.Id))
                           .ToDictionary(r => r.Id);
        ViewBag.Filter = f;
        ViewBag.MyId = AdminId;
        ViewBag.Title = "Approve Fund";
        return View(rows);
    }

    // POST: /SolarPanelAdmin/Funds/ApproveEntry
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
