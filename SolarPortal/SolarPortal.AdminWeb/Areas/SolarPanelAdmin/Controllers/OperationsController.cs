using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using SolarPortal.Application.DTOs;
using SolarPortal.Application.Interfaces;
using SolarPortal.Application.Interfaces.Services;
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

    public OperationsController(IUnitOfWork uow, ISolarRequestService requestService,
        IFileUploadService fileUploadService, UserManager<ApplicationUser> userManager)
    {
        _uow = uow;
        _requestService = requestService;
        _fileUploadService = fileUploadService;
        _userManager = userManager;
    }

    // Helper: apply state/city/status filters on top of stage filter.
    // When showHistory = true, also include requests that have already moved past
    // this stage, so admins can audit previously-processed entries.
    private async Task<IEnumerable<SolarRequest>> FilterAsync(
        ProjectStatus stage, string? state, string? city,
        ConnectionType? connType = null, bool showHistory = false)
    {
        IEnumerable<SolarRequest> all;
        if (showHistory)
        {
            // Include all requests at-or-past this stage
            all = await _uow.SolarRequests.FindAsync(x => (int)x.CurrentStage >= (int)stage);
        }
        else
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
        // Distinct states from all requests so dropdown is populated
        var allStates = (await _uow.SolarRequests.GetAllAsync())
            .Select(x => x.State).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().OrderBy(s => s).ToList();
        ViewBag.States = allStates;
        ViewBag.Workers = (await _uow.Workers.FindAsync(w => w.IsAvailable))
            .OrderBy(w => w.Name).ToList();
    }

    // --- Meter Dispatch ---
    // Spec flow: PM Surya Ghar → Meter Dispatch → Site Survey → Material Dispatch.
    // After admin approves PM Surya Ghar, the project's CurrentStage becomes MeterDispatch.
    public async Task<IActionResult> MeterDispatch(string? state, string? city, string? filter)
    {
        var showHistory = string.Equals(filter, "all", StringComparison.OrdinalIgnoreCase);
        ViewBag.Filter = showHistory ? "all" : "pending";
        var requests = await FilterAsync(ProjectStatus.MeterDispatch, state, city, showHistory: showHistory);
        await PopulateFilterViewBags(state, city, requests);
        ViewBag.Title = "Meter Dispatch";
        ViewBag.Op = "meter";
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
        var showHistory = string.Equals(filter, "all", StringComparison.OrdinalIgnoreCase);
        ViewBag.Filter = showHistory ? "all" : "pending";
        var requests = await FilterAsync(ProjectStatus.MaterialDispatch, state, city, showHistory: showHistory);
        await PopulateFilterViewBags(state, city, requests);
        ViewBag.Title = "Material Dispatch";
        ViewBag.Op = "material";
        return View("OperationsList", requests);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SubmitMaterialDispatch(int requestId, string? materialDetails,
        DateTime? dispatchDate, string? vehicleDetails, string? remark, int? workerId, IFormFile? dispatchDoc)
    {
        try
        {
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
        var showHistory = string.Equals(filter, "all", StringComparison.OrdinalIgnoreCase);
        ViewBag.Filter = showHistory ? "all" : "pending";
        var requests = await FilterAsync(ProjectStatus.Installation, state, city, showHistory: showHistory);
        await PopulateFilterViewBags(state, city, requests);
        ViewBag.Title = "Installation";
        ViewBag.Op = "installation";
        return View("OperationsList", requests);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SubmitInstallation(int requestId, DateTime? installationDate,
        string? notes, string? remark, int? workerId, IFormFile? completionPhoto)
    {
        try
        {
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
        var showHistory = string.Equals(filter, "all", StringComparison.OrdinalIgnoreCase);
        ViewBag.Filter = showHistory ? "all" : "pending";
        var requests = await FilterAsync(ProjectStatus.DCRUpdate, state, city, ConnectionType.Domestic, showHistory: showHistory);
        await PopulateFilterViewBags(state, city, requests);
        ViewBag.Title = "DCR Update";
        ViewBag.Op = "dcr";
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

            var dcr = new DCRDocument
            {
                SolarRequestId = requestId,
                DCRNumber = dcrNumber,
                DCRDate = dcrDate ?? DateTime.UtcNow,
                DocumentPath = docPath,
                Remark = remark,
                ExtractedData = SimulateOCR(dcrNumber),
                IsVerified = true
            };

            await _uow.DCRDocuments.AddAsync(dcr);
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
