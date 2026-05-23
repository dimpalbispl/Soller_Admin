using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using SolarPortal.Application.DTOs;
using SolarPortal.Application.Interfaces;
using SolarPortal.Application.Interfaces.Services;
using SolarPortal.Domain.Entities;
using SolarPortal.Domain.Enums;

namespace SolarPortal.AdminWeb.Areas.SolarPanelAdmin.Controllers;

/// <summary>
/// Admin verifies PM Surya Ghar documents uploaded by users.
/// Once approved, project moves to MeterDispatch.
/// </summary>
[Area("SolarPanelAdmin")]
[Authorize(Roles = "Admin")]
public class PMSuryaController : Controller
{
    private readonly IUnitOfWork _uow;
    private readonly IPMDocumentService _pmDocs;
    private readonly ISolarRequestService _requestService;
    private readonly INotificationService _notifications;
    private readonly UserManager<ApplicationUser> _userManager;

    public PMSuryaController(
        IUnitOfWork uow,
        IPMDocumentService pmDocs,
        ISolarRequestService requestService,
        INotificationService notifications,
        UserManager<ApplicationUser> userManager)
    {
        _uow = uow;
        _pmDocs = pmDocs;
        _requestService = requestService;
        _notifications = notifications;
        _userManager = userManager;
    }

    // GET: /Admin/PMSurya/Index
    // Default view shows only requests pending verification (stage = PMSurvey).
    // ?filter=approved → already-advanced (past PMSurvey)
    // ?filter=rejected → requests whose PM Surya documents were rejected
    // ?filter=all      → full history (everything ever in PMSurya+)
    public async Task<IActionResult> Index(string? filter)
    {
        var f = (filter ?? "pending").ToLowerInvariant();
        IEnumerable<SolarRequest> requests;

        if (f == "all")
        {
            // Show every request that has at least reached PM Surya stage,
            // including those that have already advanced past it.
            requests = await _uow.SolarRequests.FindAsync(x =>
                x.CurrentStage == ProjectStatus.PMSurvey ||
                x.CurrentStage == ProjectStatus.MeterDispatch ||
                x.CurrentStage == ProjectStatus.SiteSurvey ||
                x.CurrentStage == ProjectStatus.MaterialDispatch ||
                x.CurrentStage == ProjectStatus.Installation ||
                x.CurrentStage == ProjectStatus.DCRUpdate ||
                x.CurrentStage == ProjectStatus.Completed);
        }
        else if (f == "approved")
        {
            requests = await _uow.SolarRequests.FindAsync(x =>
                x.CurrentStage == ProjectStatus.MeterDispatch ||
                x.CurrentStage == ProjectStatus.SiteSurvey ||
                x.CurrentStage == ProjectStatus.MaterialDispatch ||
                x.CurrentStage == ProjectStatus.Installation ||
                x.CurrentStage == ProjectStatus.DCRUpdate ||
                x.CurrentStage == ProjectStatus.Completed);
        }
        else if (f == "rejected")
        {
            // Rejected = requests at PMSurvey stage that have at least one
            // document with ApprovalStatus = Rejected. The user can re-upload
            // and the row moves back into "pending" once they do.
            var atStage  = await _uow.SolarRequests.FindAsync(x => x.CurrentStage == ProjectStatus.PMSurvey);
            var rejected = (await _uow.PMDocuments.GetAllAsync())
                           .Where(d => d.Status == ApprovalStatus.Rejected)
                           .Select(d => d.SolarRequestId)
                           .ToHashSet();
            requests = atStage.Where(r => rejected.Contains(r.Id));
        }
        else // pending
        {
            // Only show requests at PMSurvey stage WHERE the user has actually
            // uploaded at least one document. Without docs, there's nothing for
            // the admin to verify yet — and showing empty requests in the queue
            // creates noise.
            var atStage = await _uow.SolarRequests.FindAsync(x => x.CurrentStage == ProjectStatus.PMSurvey);
            var docs    = await _uow.PMDocuments.GetAllAsync();
            var withDocs = docs.Select(d => d.SolarRequestId).ToHashSet();
            requests = atStage.Where(r => withDocs.Contains(r.Id));
        }

        ViewBag.Title = "PM Surya Ghar Verification";
        ViewBag.Filter = f;
        return View(requests.OrderByDescending(r => r.CreatedAt));
    }

    // GET: /Admin/PMSurya/Details/5
    public async Task<IActionResult> Details(int id)
    {
        var req = await _uow.SolarRequests.GetByIdAsync(id);
        if (req == null) return NotFound();

        var docs = await _pmDocs.GetByRequestIdAsync(id);
        ViewBag.Request = req;
        ViewBag.Documents = docs;
        return View();
    }

    // POST: /Admin/PMSurya/ApproveDocument
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ApproveDocument(int docId, string? remarks)
    {
        await _pmDocs.ApproveDocumentAsync(docId, remarks);
        return Json(new { success = true, message = "Document approved" });
    }

    // POST: /Admin/PMSurya/RejectDocument
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RejectDocument(int docId, string? remarks)
    {
        await _pmDocs.RejectDocumentAsync(docId, remarks);
        return Json(new { success = true, message = "Document rejected" });
    }

    // POST: /Admin/PMSurya/ApproveAndAdvance/5 — approve the whole batch and move to MeterDispatch
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ApproveAndAdvance(int requestId, string? notes)
    {
        var req = await _uow.SolarRequests.GetByIdAsync(requestId);
        if (req == null) return Json(new { success = false, message = "Request not found" });

        // Mark all PM docs approved
        var docs = await _uow.PMDocuments.FindAsync(d => d.SolarRequestId == requestId);
        foreach (var d in docs)
        {
            if (d.Status == ApprovalStatus.Pending)
            {
                d.Status = ApprovalStatus.Approved;
                d.Remarks = notes;
                _uow.PMDocuments.Update(d);
            }
        }
        await _uow.SaveChangesAsync();

        var adminId = _userManager.GetUserId(User)!;
        // Spec workflow: PM Surya Ghar → Site Survey → Meter Dispatch
        await _requestService.UpdateStageAsync(new UpdateSolarRequestStatusDto
        {
            Id = requestId,
            NewStage = ProjectStatus.MeterDispatch,
            Notes = notes ?? "PM Surya Ghar documents verified."
        }, adminId);

        await _notifications.CreateAsync(new CreateNotificationDto
        {
            UserId = req.UserId,
            SolarRequestId = requestId,
            Title = "PM Surya Ghar approved",
            Message = "Your documents are verified. Site survey form is now available for download.",
            NotificationType = "PMSurya"
        });

        return Json(new { success = true, message = "PM Surya Ghar approved. Project moved to Site Survey." });
    }
}
