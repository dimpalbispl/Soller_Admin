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
///
/// ── Accept-before-decide (change request point 9) ────────────────────────
/// A case has to be ACCEPTED by an admin before its documents can be approved
/// or rejected, and only the admin who accepted it may decide it. Rejecting any
/// document sends the case back to Pending and RELEASES the claim, so once the
/// user re-uploads, any admin can accept it again and carry on. Every one of
/// those steps is written to the activity log.
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
    private readonly IAdminActivityLogger _activity;
    private readonly UserManager<ApplicationUser> _userManager;

    public PMSuryaController(
        IUnitOfWork uow,
        IPMDocumentService pmDocs,
        ISolarRequestService requestService,
        INotificationService notifications,
        IFileUploadService fileUploadService,
        IAdminActivityLogger activity,
        UserManager<ApplicationUser> userManager)
    {
        _uow = uow;
        _pmDocs = pmDocs;
        _requestService = requestService;
        _notifications = notifications;
        _fileUploadService = fileUploadService;
        _activity = activity;
        _userManager = userManager;
    }

    private string CurrentAdminId => _userManager.GetUserId(User) ?? "system";
    private string? ClientIp => HttpContext.Connection.RemoteIpAddress?.ToString();

    /// <summary>Display name for the "accepted by" stamp — falls back to the id.</summary>
    private async Task<string> CurrentAdminNameAsync()
    {
        var me = await _userManager.GetUserAsync(User);
        return !string.IsNullOrWhiteSpace(me?.FullName) ? me!.FullName!
             : !string.IsNullOrWhiteSpace(me?.UserName) ? me!.UserName!
             : CurrentAdminId;
    }

    /// <summary>
    /// Gate for every decision on a PM Surya case: it must be accepted, and by
    /// the admin making the call. Returns null when the caller may proceed,
    /// otherwise the JSON error to send back.
    /// </summary>
    private IActionResult? BlockIfNotMine(SolarRequest req)
    {
        if (!req.IsPmSuryaAccepted)
            return Json(new
            {
                success = false,
                message = "Accept this PM Surya Ghar case first. Only the admin who accepts it can approve or reject its documents."
            });

        if (!string.Equals(req.PmSuryaAcceptedBy, CurrentAdminId, StringComparison.OrdinalIgnoreCase))
            return Json(new
            {
                success = false,
                message = $"This case was accepted by {req.PmSuryaAcceptedByName ?? req.PmSuryaAcceptedBy}. Only they can approve or reject it."
            });

        return null;
    }

    // GET: /Admin/PMSurya/Index
    // Default view is "all" (full history) per spec — report kholte hi poora
    // data dikhna chahiye, phir admin tab se narrow kare.
    // ?filter=pending  → awaiting verification (stage = PMSurvey)
    // ?filter=approved → already-advanced (past PMSurvey)
    // ?filter=rejected → requests whose PM Surya documents were rejected
    public async Task<IActionResult> Index(string? filter)
    {
        var f = (filter ?? "all").ToLowerInvariant();
        IEnumerable<SolarRequest> requests;

        // Requests still sitting at PMSurvey are only worth showing once the user
        // has actually uploaded something. Admin-uploaded PMApprovalDocument rows
        // don't count — woh approval ke time bante hain, user ke docs nahi hote.
        var requestIdsWithUserDocs = (await _uow.PMDocuments.GetAllAsync())
            .Where(d => d.DocumentType != DocumentType.PMApprovalDocument)
            .Select(d => d.SolarRequestId)
            .ToHashSet();

        if (f == "all")
        {
            // Show every request that has at least reached PM Surya stage,
            // including those that have already advanced past it. Stage-PMSurvey
            // rows without any uploaded document are skipped — unka verify karne
            // ke liye kuch hai hi nahi.
            var all = await _uow.SolarRequests.FindAsync(x =>
                x.CurrentStage == ProjectStatus.PMSurvey ||
                x.CurrentStage == ProjectStatus.MeterDispatch ||
                x.CurrentStage == ProjectStatus.SiteSurvey ||
                x.CurrentStage == ProjectStatus.MaterialDispatch ||
                x.CurrentStage == ProjectStatus.Installation ||
                x.CurrentStage == ProjectStatus.DCRUpdate ||
                x.CurrentStage == ProjectStatus.Completed);

            requests = all.Where(r => r.CurrentStage != ProjectStatus.PMSurvey ||
                                      requestIdsWithUserDocs.Contains(r.Id));
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
            requests = atStage.Where(r => requestIdsWithUserDocs.Contains(r.Id));
        }

        ViewBag.Title = "PM Surya Ghar Verification";
        ViewBag.Filter = f;
            
            // Enrich user active status info
            // Newest request first. Id is the tie-breaker because several requests
            // can share a CreatedAt (bulk/seeded rows, or two in the same second),
            // and without it those rows come back in whatever order the provider
            // happens to return - which reads as "not sorted at all".
            var requestList = requests
                .OrderByDescending(r => r.CreatedAt)
                .ThenByDescending(r => r.Id)
                .ToList();
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

        // Point 9: the page needs to know whether this case is claimed, by whom,
        // and whether that is the admin looking at it — the buttons key off this.
        ViewBag.AcceptedBy = req.PmSuryaAcceptedBy;
        ViewBag.AcceptedByName = req.PmSuryaAcceptedByName;
        ViewBag.AcceptedAt = req.PmSuryaAcceptedAt;
        ViewBag.IsMine = req.IsPmSuryaAccepted &&
                         string.Equals(req.PmSuryaAcceptedBy, CurrentAdminId, StringComparison.OrdinalIgnoreCase);

        ViewBag.ActivityLog = await _activity.GetForEntityAsync("PMSurya", id.ToString());
        return View();
    }

    // POST: /Admin/PMSurya/Accept — claim the case. First admin to click owns the
    // decision until they reject a document (which releases it again).
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Accept(int requestId)
    {
        var req = await _uow.SolarRequests.GetByIdAsync(requestId);
        if (req == null) return Json(new { success = false, message = "Request not found" });

        var meId = CurrentAdminId;

        if (req.IsPmSuryaAccepted)
        {
            // Already mine → nothing to do; already someone else's → refuse, so two
            // admins can never both believe they own the case.
            return string.Equals(req.PmSuryaAcceptedBy, meId, StringComparison.OrdinalIgnoreCase)
                ? Json(new { success = true, message = "You have already accepted this case." })
                : Json(new { success = false, message = $"Already accepted by {req.PmSuryaAcceptedByName ?? req.PmSuryaAcceptedBy}." });
        }

        var meName = await CurrentAdminNameAsync();
        req.PmSuryaAcceptedBy = meId;
        req.PmSuryaAcceptedByName = meName;
        req.PmSuryaAcceptedAt = DateTime.UtcNow;
        req.UpdatedAt = DateTime.UtcNow;
        _uow.SolarRequests.Update(req);
        await _uow.SaveChangesAsync();

        await _activity.LogAsync(meId, "PMSurya.Accept", "PMSurya", requestId.ToString(),
            $"{meName} accepted PM Surya Ghar case {req.RequestNumber}.", ClientIp);

        return Json(new { success = true, message = "Case accepted. You can now approve or reject its documents." });
    }

    // POST: /Admin/PMSurya/ApproveDocument
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ApproveDocument(int docId, string? remarks)
    {
        var doc = await _uow.PMDocuments.GetByIdAsync(docId);
        if (doc == null) return Json(new { success = false, message = "Document not found" });

        var req = await _uow.SolarRequests.GetByIdAsync(doc.SolarRequestId);
        if (req == null) return Json(new { success = false, message = "Request not found" });

        var blocked = BlockIfNotMine(req);
        if (blocked != null) return blocked;

        await _pmDocs.ApproveDocumentAsync(docId, remarks);

        await _activity.LogAsync(CurrentAdminId, "PMSurya.ApproveDocument", "PMSurya", req.Id.ToString(),
            $"Approved document {doc.DocumentType} (#{docId}) on {req.RequestNumber}." +
            (string.IsNullOrWhiteSpace(remarks) ? "" : $" Remarks: {remarks}"), ClientIp);

        return Json(new { success = true, message = "Document approved" });
    }

    // POST: /Admin/PMSurya/RejectDocument
    //
    // Point 9: a rejection puts the case back to Pending AND releases the claim.
    // The user has to re-upload, and whoever is free at that point can accept it —
    // it does not have to be the admin who rejected it.
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RejectDocument(int docId, string? remarks)
    {
        var doc = await _uow.PMDocuments.GetByIdAsync(docId);
        if (doc == null) return Json(new { success = false, message = "Document not found" });

        var req = await _uow.SolarRequests.GetByIdAsync(doc.SolarRequestId);
        if (req == null) return Json(new { success = false, message = "Request not found" });

        var blocked = BlockIfNotMine(req);
        if (blocked != null) return blocked;

        if (string.IsNullOrWhiteSpace(remarks))
            return Json(new { success = false, message = "Please give a reason so the user knows what to re-upload." });

        var rejectedBy = req.PmSuryaAcceptedByName ?? CurrentAdminId;

        await _pmDocs.RejectDocumentAsync(docId, remarks);

        // Release the claim so the case is free again after re-upload.
        req.PmSuryaAcceptedBy = null;
        req.PmSuryaAcceptedByName = null;
        req.PmSuryaAcceptedAt = null;
        req.UpdatedAt = DateTime.UtcNow;
        _uow.SolarRequests.Update(req);
        await _uow.SaveChangesAsync();

        await _activity.LogAsync(CurrentAdminId, "PMSurya.RejectDocument", "PMSurya", req.Id.ToString(),
            $"{rejectedBy} rejected document {doc.DocumentType} (#{docId}) on {req.RequestNumber}. " +
            $"Reason: {remarks}. Case returned to Pending and released.", ClientIp);

        await _notifications.CreateAsync(new CreateNotificationDto
        {
            UserId = req.UserId,
            SolarRequestId = req.Id,
            Title = "PM Surya Ghar document rejected",
            Message = $"Your {doc.DocumentType} was rejected. Reason: {remarks}. Please upload it again.",
            NotificationType = "PMSurya"
        });

        return Json(new
        {
            success = true,
            released = true,
            message = "Document rejected. The case is back to Pending — any admin can accept it once the user re-uploads."
        });
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

        // Point 9: final approval is a decision like any other — it needs the claim.
        var blocked = BlockIfNotMine(req);
        if (blocked != null) return blocked;

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

        // ── Approval document bhi mandatory hai (spec: "PM Surya Ghar ID aur
        //    document upload nahi karta tab tak next stage me nahi ja sakta").
        //    Satisfy hota hai ya to abhi attach ki gayi file se, ya request par
        //    pehle se maujood PMApprovalDocument se. ─────────────────────────
        var hasNewApprovalDoc = approvalDocs != null &&
                                approvalDocs.Any(f => f != null && f.Length > 0);
        var hasExistingApprovalDoc = docs.Any(d => d.DocumentType == DocumentType.PMApprovalDocument);
        if (!hasNewApprovalDoc && !hasExistingApprovalDoc)
        {
            return Json(new
            {
                success = false,
                message = "PM Surya Ghar approval document is required. Attach at least one document before final approval."
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

        var adminId = CurrentAdminId;
        // Point 3: PM approve opens Meter Dispatch AND Site Survey together. The
        // stage lands on MeterDispatch, but neither queue waits for the other —
        // OperationsController.SubmitMeterDispatch and SiteSurveyController.Approve
        // each check whether the OTHER leg is finished before moving the project on
        // to Material Dispatch, so the two can be done in any order.
        await _requestService.UpdateStageAsync(new UpdateSolarRequestStatusDto
        {
            Id = requestId,
            NewStage = ProjectStatus.MeterDispatch,
            Notes = notes ?? "PM Surya Ghar documents verified."
        }, adminId);

        await _activity.LogAsync(adminId, "PMSurya.Approve", "PMSurya", requestId.ToString(),
            $"Approved PM Surya Ghar for {req.RequestNumber}. " +
            $"PM Surya ID: {req.PmSuryaApplicationNo}. Meter Dispatch & Site Survey opened." +
            (string.IsNullOrWhiteSpace(notes) ? "" : $" Notes: {notes}"), ClientIp);

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
