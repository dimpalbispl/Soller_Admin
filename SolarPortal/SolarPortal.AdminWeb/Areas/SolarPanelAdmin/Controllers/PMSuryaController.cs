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
    private readonly IFileUploadService _fileUploadService;
    private readonly UserManager<ApplicationUser> _userManager;

    public PMSuryaController(
        IUnitOfWork uow,
        IPMDocumentService pmDocs,
        ISolarRequestService requestService,
        INotificationService notifications,
        IFileUploadService fileUploadService,
        UserManager<ApplicationUser> userManager)
    {
        _uow = uow;
        _pmDocs = pmDocs;
        _requestService = requestService;
        _notifications = notifications;
        _fileUploadService = fileUploadService;
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
            
            // Enrich user active status info
            var requestList = requests.OrderByDescending(r => r.CreatedAt).ToList();
            var userIds = requestList.Select(r => r.UserId).Distinct().ToList();
            var users = new Dictionary<string, ApplicationUser>();
            
            foreach (var uid in userIds)
            {
                var user = await _userManager.FindByIdAsync(uid);
                if (user != null)
                    users[uid] = user;
            }
            
            ViewBag.UserStatuses = users;
            
            return View(requestList);
    }

    // GET: /Admin/PMSurya/Details/5
    public async Task<IActionResult> Details(int id)
    {
        var req = await _uow.SolarRequests.GetByIdAsync(id);
        if (req == null) return NotFound();

        // Admin-uploaded PM approval documents are ALWAYS auto-approved — admin
        // khud upload karta hai to approve/reject ka koi matlab nahi. Purane
        // (legacy) rows jo Pending reh gaye the unhe yahan self-heal kar dete
        // hain taaki table mein Approve/Reject kabhi na dikhe.
        var pendingApprovalDocs = (await _uow.PMDocuments.FindAsync(d =>
                d.SolarRequestId == id &&
                d.DocumentType == DocumentType.PMApprovalDocument &&
                d.Status == ApprovalStatus.Pending)).ToList();
        if (pendingApprovalDocs.Any())
        {
            foreach (var d in pendingApprovalDocs)
            {
                d.Status = ApprovalStatus.Approved;
                d.UpdatedAt = DateTime.UtcNow;
                _uow.PMDocuments.Update(d);
            }
            await _uow.SaveChangesAsync();
        }

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

    // POST: /Admin/PMSurya/ApproveAndAdvance/5 — approve the whole batch, store the
    // PM Surya Ghar application no. + admin approval docs, and open Meter Dispatch +
    // Site Survey together.
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ApproveAndAdvance(int requestId, string? notes,
                                                       string? pmSuryaApplicationNo,
                                                       List<IFormFile>? approvalDocs)
    {
        var req = await _uow.SolarRequests.GetByIdAsync(requestId);
        if (req == null) return Json(new { success = false, message = "Request not found" });

        var docs = (await _uow.PMDocuments.FindAsync(d => d.SolarRequestId == requestId)).ToList();

        // ── Task 10: approval blocked until ALL required documents are present and
        //    none is still in Rejected state. ──────────────────────────────────────
        // Required user document types (PM Surya Ghar Application is now admin-uploaded
        // per Task 9, so it is NOT part of the user-required set).
        var requiredTypes = new[]
        {
            DocumentType.AadharCard,
            DocumentType.PANCard,
            DocumentType.LightBill,
            DocumentType.BankPassbook,
            DocumentType.PropertyDocument,
            DocumentType.GPSPhoto
        };
        var presentTypes = docs.Select(d => d.DocumentType).ToHashSet();
        var missing = requiredTypes.Where(t => !presentTypes.Contains(t)).ToList();
        if (missing.Any())
        {
            return Json(new
            {
                success = false,
                message = "Cannot approve yet — these documents are still missing: " +
                          string.Join(", ", missing)
            });
        }
        if (docs.Any(d => requiredTypes.Contains(d.DocumentType) && d.Status == ApprovalStatus.Rejected))
        {
            return Json(new
            {
                success = false,
                message = "Some documents are still Rejected. Approve each document (or wait for the user to re-upload) before final approval."
            });
        }

        // ── PM Surya Ghar ID is mandatory for final approval. Jab tak admin
        //    ID upload/enter nahi karta, batch approve + stage advance blocked
        //    rahega (UI bhi ID form ko tab tak visible rakhta hai). ──────────
        if (string.IsNullOrWhiteSpace(pmSuryaApplicationNo) &&
            string.IsNullOrWhiteSpace(req.PmSuryaApplicationNo))
        {
            return Json(new
            {
                success = false,
                message = "PM Surya Ghar ID No. is required. Enter the ID before final approval."
            });
        }

        // Mark all required PM docs approved
        foreach (var d in docs)
        {
            if (d.Status == ApprovalStatus.Pending)
            {
                d.Status = ApprovalStatus.Approved;
                d.Remarks = notes;
                _uow.PMDocuments.Update(d);
            }
        }

        // ── Task 11: PM Surya Ghar application no. + admin-uploaded approval docs ──
        if (!string.IsNullOrWhiteSpace(pmSuryaApplicationNo))
            req.PmSuryaApplicationNo = pmSuryaApplicationNo.Trim();

        if (approvalDocs != null)
        {
            foreach (var file in approvalDocs.Where(f => f != null && f.Length > 0))
            {
                var (ok, path, _) = await _fileUploadService.UploadAsync(file, $"{req.RequestNumber}/pmsurya-approval");
                if (ok && !string.IsNullOrWhiteSpace(path))
                {
                    var uploaded = await _pmDocs.UploadDocumentAsync(
                        solarRequestId: requestId,
                        documentType: DocumentType.PMApprovalDocument,
                        fileName: Path.GetFileNameWithoutExtension(file.FileName),
                        filePath: path,
                        contentType: file.ContentType,
                        fileSize: file.Length);
                    // Admin ka upload hai — direct Approved (koi review nahi chahiye).
                    await _pmDocs.ApproveDocumentAsync(uploaded.Id, null);
                }
            }
        }

        _uow.SolarRequests.Update(req);
        await _uow.SaveChangesAsync();

        var adminId = _userManager.GetUserId(User)!;
        // Task 12: PM approve — open Meter Dispatch and Site Survey simultaneously.
        // We advance the request to MeterDispatch but SiteSurvey remains available
        // for the user to submit/complete; both can be done in any order.
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
            Message = "Your documents are verified. Meter Dispatch and Site Survey are now both available — you can fill the Site Survey right away.",
            NotificationType = "PMSurya"
        });

        return Json(new { success = true, message = "PM Surya Ghar approved. Meter Dispatch & Site Survey are now open." });
    }

    // POST: /Admin/PMSurya/DeleteApprovalDocument — sirf admin-uploaded
    // PMApprovalDocument delete ho sakta hai (user ke documents nahi).
    // Delete ke baad admin wapas naya approval document upload kar sakta hai.
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteApprovalDocument(int docId)
    {
        var doc = await _uow.PMDocuments.GetByIdAsync(docId);
        if (doc == null)
            return Json(new { success = false, message = "Document not found" });

        if (doc.DocumentType != DocumentType.PMApprovalDocument)
            return Json(new { success = false, message = "Only admin-uploaded PM approval documents can be deleted here." });

        // Physical file bhi hata do taaki orphan files na bachein.
        if (!string.IsNullOrWhiteSpace(doc.FilePath))
            _fileUploadService.DeleteFile(doc.FilePath);

        await _pmDocs.DeleteDocumentAsync(docId);
        return Json(new { success = true, message = "Approval document deleted. You can upload a new one." });
    }

    // POST: /Admin/PMSurya/UploadApprovalDocument — final approval ke BAAD bhi
    // admin approval document(s) upload/replace kar sakta hai. Uploads direct
    // Approved status ke saath save hote hain.
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UploadApprovalDocument(int requestId, List<IFormFile>? files)
    {
        var req = await _uow.SolarRequests.GetByIdAsync(requestId);
        if (req == null)
            return Json(new { success = false, message = "Request not found" });

        var validFiles = (files ?? new List<IFormFile>()).Where(f => f != null && f.Length > 0).ToList();
        if (!validFiles.Any())
            return Json(new { success = false, message = "Please select at least one file to upload." });

        var count = 0;
        foreach (var file in validFiles)
        {
            var (ok, path, _) = await _fileUploadService.UploadAsync(file, $"{req.RequestNumber}/pmsurya-approval");
            if (ok && !string.IsNullOrWhiteSpace(path))
            {
                var uploaded = await _pmDocs.UploadDocumentAsync(
                    solarRequestId: requestId,
                    documentType: DocumentType.PMApprovalDocument,
                    fileName: Path.GetFileNameWithoutExtension(file.FileName),
                    filePath: path,
                    contentType: file.ContentType,
                    fileSize: file.Length);
                await _pmDocs.ApproveDocumentAsync(uploaded.Id, null);
                count++;
            }
        }

        return Json(new { success = count > 0, message = count > 0
            ? $"{count} approval document(s) uploaded."
            : "Upload failed. Please try again." });
    }
}
