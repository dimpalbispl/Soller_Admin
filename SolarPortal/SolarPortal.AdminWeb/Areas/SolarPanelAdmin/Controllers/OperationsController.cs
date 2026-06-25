using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using SolarPortal.Application.DTOs;
using SolarPortal.Application.Interfaces;
using SolarPortal.Application.Interfaces.Services;
using SolarPortal.Application.Services;
using SolarPortal.Domain.Entities;
using SolarPortal.Domain.Enums;

namespace SolarPortal.AdminWeb.Areas.SolarPanelAdmin.Controllers;

[Area("SolarPanelAdmin")]
[Authorize(Roles = "Admin")]
public class OperationsController : Controller
{
    private readonly IUnitOfWork _uow;
    private readonly ISolarRequestService _requestService;
    private readonly IFileUploadService _fileUploadService;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IStateService _states;

    public OperationsController(IUnitOfWork uow, ISolarRequestService requestService,
        IFileUploadService fileUploadService, UserManager<ApplicationUser> userManager,
        IStateService states)
    {
        _uow = uow;
        _requestService = requestService;
        _fileUploadService = fileUploadService;
        _userManager = userManager;
        _states = states;
    }

    // Helper: apply state/city/status filters on top of stage filter.
    //
    // Filter semantics for operations queues (per spec — admin needs to see
    // every record after action, not just the pending queue):
    //   pending  → requests AT the given stage (the actionable queue)
    //   approved → requests that have moved PAST this stage (i.e. action done)
    //   rejected → DCR has approve/reject; rejected = stage at DCRUpdate with
    //              latest DCRDocument.ApprovalStatus == Rejected. Other
    //              operations (dispatch) don't have a reject concept, so we
    //              return an empty set for those (the UI still shows the tab
    //              for consistency).
    //   all      → everything at-or-past the stage (history)
    private async Task<IEnumerable<SolarRequest>> FilterAsync(
        ProjectStatus stage, string? state, string? city,
        ConnectionType? connType = null, bool showHistory = false,
        string filterMode = "pending", string? op = null)
    {
        IEnumerable<SolarRequest> all;

        var mode = (filterMode ?? "pending").ToLowerInvariant();
        if (mode == "all" || showHistory)
        {
            all = await _uow.SolarRequests.FindAsync(x => (int)x.CurrentStage >= (int)stage);
        }
        else if (mode == "approved")
        {
            // Past this stage = the operation completed for these requests.
            all = await _uow.SolarRequests.FindAsync(x => (int)x.CurrentStage > (int)stage);
        }
        else if (mode == "rejected")
        {
            // DCR is the only operation with an admin approve/reject. Other
            // dispatch modes (meter/material/installation) don't have a
            // rejection state — they're admin-driven actions, not approvals.
            if (string.Equals(op, "dcr", StringComparison.OrdinalIgnoreCase))
            {
                var rejectedIds = (await _uow.DCRDocuments.FindAsync(
                                    d => d.ApprovalStatus == ApprovalStatus.Rejected))
                                 .Select(d => d.SolarRequestId)
                                 .ToHashSet();
                all = (await _uow.SolarRequests.GetAllAsync())
                      .Where(r => rejectedIds.Contains(r.Id));
            }
            else
            {
                all = Enumerable.Empty<SolarRequest>();
            }
        }
        else // pending
        {
            all = await _uow.SolarRequests.FindAsync(x => x.CurrentStage == stage);
        }

        IEnumerable<SolarRequest> q = all;
        if (!string.IsNullOrWhiteSpace(state))
            q = q.Where(x => x.State.Equals(state, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(city))
            q = q.Where(x => x.City.Contains(city, StringComparison.OrdinalIgnoreCase));
        if (connType.HasValue)
            q = q.Where(x => x.ConnectionType == connType.Value);
        return q.OrderByDescending(x => x.CreatedAt).ToList();
    }

    private async Task PopulateFilterViewBags(string? state, string? city, IEnumerable<SolarRequest> rows)
    {
        ViewBag.FilterState = state;
        ViewBag.FilterCity = city;
        // State filter dropdown comes from the legacy M_StateDivMaster table
        // (same source as every other state dropdown in the app) — not from the
        // distinct states of existing requests. This way the filter always lists
        // every real state even before any request from that state exists.
        var allStates = (await _states.GetActiveAsync())
            .Select(s => s.StateName)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct()
            .OrderBy(s => s)
            .ToList();
        ViewBag.States = allStates;
        ViewBag.Workers = (await _uow.Workers.FindAsync(w => w.IsAvailable))
            .OrderBy(w => w.Name).ToList();
    }

    // Load the dispatch-detail rows for the given operation type and expose them
    // on ViewBag so the OperationsList view can render them in "All (history)" mode.
    // Per spec: "history mein dispatch details bhi show honi chahiye" — the
    // queue list shows only request-level info by default; the matching detail
    // (meter number, dispatch date, document path, remark, etc.) lives in the
    // separate MeterDispatch/MaterialDispatch/Installation/DCRDocument tables.
    //
    // The view checks `ViewBag.Filter == "all"` and renders an extra "Details"
    // column populated from these dictionaries (keyed by SolarRequestId, picking
    // the latest row when multiple exist).
    private async Task PopulateOperationDetailsAsync(string op, IEnumerable<SolarRequest> rows)
    {
        var ids = rows.Select(r => r.Id).ToHashSet();
        if (!ids.Any()) return;

        switch (op)
        {
            case "meter":
                var meters = (await _uow.MeterDispatches.FindAsync(m => ids.Contains(m.SolarRequestId)))
                             .GroupBy(m => m.SolarRequestId)
                             .ToDictionary(g => g.Key, g => g.OrderByDescending(m => m.CreatedAt).First());
                ViewBag.MeterDetails = meters;
                break;
            case "material":
                var materials = (await _uow.MaterialDispatches.FindAsync(m => ids.Contains(m.SolarRequestId)))
                                .GroupBy(m => m.SolarRequestId)
                                .ToDictionary(g => g.Key, g => g.OrderByDescending(m => m.CreatedAt).First());
                // Task 17: FindAsync does NOT eager-load the AssignedWorker navigation,
                // so the assigned worker was showing blank. Attach it manually.
                await AttachWorkersAsync(materials.Values.Select(m => (m.AssignedWorkerId, (Action<Worker>)(w => m.AssignedWorker = w))));
                ViewBag.MaterialDetails = materials;
                break;
            case "installation":
                var installs = (await _uow.Installations.FindAsync(i => ids.Contains(i.SolarRequestId)))
                               .GroupBy(i => i.SolarRequestId)
                               .ToDictionary(g => g.Key, g => g.OrderByDescending(i => i.CreatedAt).First());
                // Task 17 (same latent bug): attach the installer worker too.
                await AttachWorkersAsync(installs.Values.Select(i => (i.AssignedWorkerId, (Action<Worker>)(w => i.AssignedWorker = w))));
                ViewBag.InstallationDetails = installs;
                break;
            case "dcr":
                var dcrs = (await _uow.DCRDocuments.FindAsync(d => ids.Contains(d.SolarRequestId)))
                           .GroupBy(d => d.SolarRequestId)
                           .ToDictionary(g => g.Key, g => g.OrderByDescending(d => d.CreatedAt).First());
                ViewBag.DCRDetails = dcrs;
                break;
        }
    }

    // Task 17: resolve worker names for entities whose AssignedWorker navigation was
    // not eager-loaded. We load every referenced worker once, then run each setter to
    // attach the matching Worker onto its entity. Works even for workers that have since
    // been marked unavailable (ViewBag.Workers only contains *available* ones).
    private async Task AttachWorkersAsync(IEnumerable<(int? workerId, Action<Worker> setter)> items)
    {
        var list = items.Where(x => x.workerId.HasValue).ToList();
        if (list.Count == 0) return;

        var workerIds = list.Select(x => x.workerId!.Value).Distinct().ToHashSet();
        var workers = (await _uow.Workers.FindAsync(w => workerIds.Contains(w.Id)))
                      .ToDictionary(w => w.Id);

        foreach (var (workerId, setter) in list)
        {
            if (workerId.HasValue && workers.TryGetValue(workerId.Value, out var worker))
                setter(worker);
        }
    }

    // --- Meter Dispatch ---
    // Spec flow: PM Surya Ghar → Meter Dispatch → Site Survey → Material Dispatch.
    // After admin approves PM Surya Ghar, the project's CurrentStage becomes MeterDispatch.
    public async Task<IActionResult> MeterDispatch(string? state, string? city, string? filter)
    {
        var f = (filter ?? "pending").ToLowerInvariant();
        var showHistory = f == "all";
        ViewBag.Filter = f;
        var requests = await FilterAsync(ProjectStatus.MeterDispatch, state, city, showHistory: showHistory, filterMode: f, op: "meter");
        await PopulateFilterViewBags(state, city, requests);
        ViewBag.Title = "Meter Dispatch";
        ViewBag.Op = "meter";
        await PopulateOperationDetailsAsync("meter", requests);
        return View("OperationsList", requests);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SubmitMeterDispatch(int requestId, string meterNumber,
        string meterType, DateTime? dispatchDate, string? remark, IFormFile? dispatchDoc)
    {
        try
        {
            string? docPath = null;
            if (dispatchDoc != null)
            {
                var (ok, path, err) = await _fileUploadService.UploadAsync(dispatchDoc, "dispatch/meter");
                if (!ok) return Json(new { success = false, message = $"Document upload failed: {err}" });
                docPath = path;
            }

            var dispatch = new MeterDispatch
            {
                SolarRequestId = requestId,
                MeterNumber = meterNumber,
                MeterType = meterType,
                DispatchDate = dispatchDate ?? DateTime.UtcNow,
                DispatchDocumentPath = docPath,
                Remark = remark,
                IsDispatched = true,
                DispatchedBy = _userManager.GetUserId(User)
            };

            await _uow.MeterDispatches.AddAsync(dispatch);
            await _uow.SaveChangesAsync();

            // Advance to SiteSurvey (Meter Dispatch happens BEFORE Site Survey per spec).
            var stageResult = await _requestService.UpdateStageAsync(new UpdateSolarRequestStatusDto
            {
                Id = requestId,
                NewStage = ProjectStatus.SiteSurvey,
                Notes = $"Meter {meterNumber} dispatched on {dispatch.DispatchDate:dd/MM/yyyy}"
            }, _userManager.GetUserId(User)!);

            if (!stageResult.IsSuccess)
                return Json(new { success = false, message = $"Stage update failed: {stageResult.Message ?? string.Join("; ", stageResult.Errors)}" });

            return Json(new { success = true, message = $"Meter {meterNumber} dispatched. Project moved to Site Survey." });
        }
        catch (Exception ex)
        {
            var detail = ex.InnerException?.Message ?? ex.Message;
            return Json(new { success = false, message = $"Meter dispatch failed: {detail}" });
        }
    }

    // --- Material Dispatch ---
    public async Task<IActionResult> MaterialDispatch(string? state, string? city, string? filter)
    {
        var f = (filter ?? "pending").ToLowerInvariant();
        var showHistory = f == "all";
        ViewBag.Filter = f;
        var requests = await FilterAsync(ProjectStatus.MaterialDispatch, state, city, showHistory: showHistory, filterMode: f, op: "material");
        await PopulateFilterViewBags(state, city, requests);
        ViewBag.Title = "Material Dispatch";
        ViewBag.Op = "material";
        await PopulateOperationDetailsAsync("material", requests);
        return View("OperationsList", requests);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SubmitMaterialDispatch(int requestId, string? materialDetails,
        DateTime? dispatchDate, string? vehicleDetails, string? remark, int? workerId, IFormFile? dispatchDoc)
    {
        try
        {
            // Worker assignment is mandatory (also enforced on the client).
            if (!workerId.HasValue || workerId.Value <= 0)
                return Json(new { success = false, message = "Please assign a despatch person (worker) before dispatching." });

            string? docPath = null;
            if (dispatchDoc != null)
            {
                var (ok, path, err) = await _fileUploadService.UploadAsync(dispatchDoc, "dispatch/material");
                if (!ok) return Json(new { success = false, message = $"Document upload failed: {err}" });
                docPath = path;
            }

            var dispatch = new MaterialDispatch
            {
                SolarRequestId = requestId,
                MaterialDetails = materialDetails,
                DispatchDate = dispatchDate ?? DateTime.UtcNow,
                VehicleDetails = vehicleDetails,
                DispatchDocumentPath = docPath,
                Remark = remark,
                AssignedWorkerId = workerId,
                IsDispatched = true,
                DispatchedBy = _userManager.GetUserId(User)
            };

            await _uow.MaterialDispatches.AddAsync(dispatch);
            await _uow.SaveChangesAsync();

            var stageResult = await _requestService.UpdateStageAsync(new UpdateSolarRequestStatusDto
            {
                Id = requestId,
                NewStage = ProjectStatus.Installation,
                Notes = $"Material dispatched on {dispatch.DispatchDate:dd/MM/yyyy}"
            }, _userManager.GetUserId(User)!);

            if (!stageResult.IsSuccess)
                return Json(new { success = false, message = $"Stage update failed: {stageResult.Message ?? string.Join("; ", stageResult.Errors)}" });

            return Json(new { success = true, message = "Material dispatched. Project moved to Installation." });
        }
        catch (Exception ex)
        {
            var detail = ex.InnerException?.Message ?? ex.Message;
            return Json(new { success = false, message = $"Material dispatch failed: {detail}" });
        }
    }

    // --- Installation ---
    public async Task<IActionResult> Installation(string? state, string? city, string? filter)
    {
        var f = (filter ?? "pending").ToLowerInvariant();
        var showHistory = f == "all";
        ViewBag.Filter = f;
        var requests = await FilterAsync(ProjectStatus.Installation, state, city, showHistory: showHistory, filterMode: f, op: "installation");
        await PopulateFilterViewBags(state, city, requests);
        ViewBag.Title = "Installation";
        ViewBag.Op = "installation";
        await PopulateOperationDetailsAsync("installation", requests);
        return View("OperationsList", requests);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SubmitInstallation(int requestId, DateTime? installationDate,
        string? notes, string? remark, int? workerId, IFormFile? completionPhoto)
    {
        try
        {
            // Worker assignment is mandatory (also enforced on the client).
            if (!workerId.HasValue || workerId.Value <= 0)
                return Json(new { success = false, message = "Please assign an installer (worker) before submitting." });

            string? photoPath = null;
            if (completionPhoto != null)
            {
                var (ok, path, err) = await _fileUploadService.UploadAsync(completionPhoto, "installation");
                if (!ok)
                    return Json(new { success = false, message = $"Photo upload failed: {err}" });
                photoPath = path;
            }

            var installation = new Installation
            {
                SolarRequestId = requestId,
                InstallationDate = installationDate ?? DateTime.UtcNow,
                Notes = notes,
                Remark = remark,
                AssignedWorkerId = workerId,
                IsCompleted = true,
                CompletedAt = DateTime.UtcNow,
                CompletionPhotoPath = photoPath
            };

            await _uow.Installations.AddAsync(installation);
            // SAVE FIRST so installation.Id is populated before the FK reference below.
            await _uow.SaveChangesAsync();

            // If a worker was assigned, record a WorkerAssignment row (now that we have a real Id).
            if (workerId.HasValue)
            {
                await _uow.WorkerAssignments.AddAsync(new WorkerAssignment
                {
                    InstallationId = installation.Id,
                    WorkerId = workerId.Value,
                    AssignedByUserId = _userManager.GetUserId(User) ?? "system",
                    AssignedDate = DateTime.UtcNow
                });
                await _uow.SaveChangesAsync();
            }

            // Look up the request to decide DCR (Domestic) vs Completed (Commercial)
            var req = await _uow.SolarRequests.GetByIdAsync(requestId);
            if (req == null)
                return Json(new { success = false, message = "Solar request not found" });

            var nextStage = req.ConnectionType == ConnectionType.Domestic
                ? ProjectStatus.DCRUpdate
                : ProjectStatus.Completed;

            var stageResult = await _requestService.UpdateStageAsync(new UpdateSolarRequestStatusDto
            {
                Id = requestId,
                NewStage = nextStage,
                Notes = $"Installation completed on {installation.InstallationDate:dd/MM/yyyy}"
            }, _userManager.GetUserId(User)!);

            if (!stageResult.IsSuccess)
                return Json(new { success = false, message = $"Stage update failed: {stageResult.Message ?? string.Join("; ", stageResult.Errors)}" });

            var msg = nextStage == ProjectStatus.DCRUpdate
                ? "Installation complete. DCR pending."
                : "Installation complete. Project completed (Commercial).";
            return Json(new { success = true, message = msg });
        }
        catch (Exception ex)
        {
            // Surface the real reason instead of a generic SweetAlert "Failed"
            var detail = ex.InnerException?.Message ?? ex.Message;
            return Json(new { success = false, message = $"Installation failed: {detail}" });
        }
    }

    // --- DCR Update (Domestic only) ---
    public async Task<IActionResult> DCRUpdate(string? state, string? city, string? filter)
    {
        var f = (filter ?? "pending").ToLowerInvariant();
        var showHistory = f == "all";
        ViewBag.Filter = f;
        var requests = await FilterAsync(ProjectStatus.DCRUpdate, state, city, ConnectionType.Domestic, showHistory: showHistory, filterMode: f, op: "dcr");
        await PopulateFilterViewBags(state, city, requests);
        ViewBag.Title = "DCR Update";
        ViewBag.Op = "dcr";
        await PopulateOperationDetailsAsync("dcr", requests);
        return View("OperationsList", requests);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SubmitDCR(int requestId, string dcrNumber,
        DateTime? dcrDate, string? remark, IFormFile? dcrDoc)
    {
        try
        {
            string? docPath = null;
            if (dcrDoc != null)
            {
                var (ok, path, err) = await _fileUploadService.UploadAsync(dcrDoc, "dcr");
                if (!ok) return Json(new { success = false, message = $"Document upload failed: {err}" });
                docPath = path;
            }

            // Upsert: the user has already submitted a DCR record (number, date, document).
            // Admin verification must UPDATE that same row — not create a duplicate. We only
            // create a new row if none exists yet.
            var dcr = (await _uow.DCRDocuments.FindAsync(d => d.SolarRequestId == requestId))
                      .OrderByDescending(d => d.Id)
                      .FirstOrDefault();
            bool isNew = dcr == null;
            if (isNew) dcr = new DCRDocument { SolarRequestId = requestId };

            dcr!.DCRNumber = dcrNumber;
            dcr.DCRDate = dcrDate ?? dcr.DCRDate ?? DateTime.UtcNow;
            if (docPath != null) dcr.DocumentPath = docPath;          // admin re-upload replaces; else keep user's
            if (!string.IsNullOrWhiteSpace(remark)) dcr.Remark = remark;
            dcr.ExtractedData = SimulateOCR(dcrNumber);
            dcr.IsVerified = true;
            dcr.ApprovalStatus = ApprovalStatus.Approved;
            dcr.ApprovedAt = DateTime.UtcNow;
            dcr.ApprovedBy = _userManager.GetUserId(User);

            if (isNew) await _uow.DCRDocuments.AddAsync(dcr);
            else _uow.DCRDocuments.Update(dcr);
            await _uow.SaveChangesAsync();

            var stageResult = await _requestService.UpdateStageAsync(new UpdateSolarRequestStatusDto
            {
                Id = requestId,
                NewStage = ProjectStatus.Completed,
                Notes = $"DCR {dcrNumber} submitted on {dcr.DCRDate:dd/MM/yyyy}"
            }, _userManager.GetUserId(User)!);

            if (!stageResult.IsSuccess)
                return Json(new { success = false, message = $"Stage update failed: {stageResult.Message ?? string.Join("; ", stageResult.Errors)}" });

            return Json(new { success = true, message = $"DCR {dcrNumber} submitted. Project completed!" });
        }
        catch (Exception ex)
        {
            var detail = ex.InnerException?.Message ?? ex.Message;
            return Json(new { success = false, message = $"DCR submit failed: {detail}" });
        }
    }

    private static string SimulateOCR(string dcrNumber) =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            DCRNumber = dcrNumber,
            ExtractedDate = DateTime.Today.ToString("dd/MM/yyyy"),
            Status = "Verified",
            Confidence = "98%"
        });
}
