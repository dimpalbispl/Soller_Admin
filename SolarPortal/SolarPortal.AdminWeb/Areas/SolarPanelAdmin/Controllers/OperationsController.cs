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
    private readonly IAdminActivityLogger _activity;
    private readonly IIncCommissionCreditService _incCommission;

    public OperationsController(IUnitOfWork uow, ISolarRequestService requestService,
        IFileUploadService fileUploadService, UserManager<ApplicationUser> userManager,
        IStateService states, IAdminActivityLogger activity,
        IIncCommissionCreditService incCommission)
    {
        _uow = uow;
        _requestService = requestService;
        _fileUploadService = fileUploadService;
        _userManager = userManager;
        _states = states;
        _activity = activity;
        _incCommission = incCommission;
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

            // DCR: jo request abhi DCRUpdate stage par hai lekin user ne DCR
            // upload hi nahi kiya, wo "All" (history) mein bhi nahi aani
            // chahiye — admin ke liye abhi koi record hai hi nahi. Stage se
            // aage badh chuki (Completed) rows history hain, wo dikhengi.
            if (string.Equals(op, "dcr", StringComparison.OrdinalIgnoreCase))
            {
                var docIds = (await _uow.DCRDocuments.GetAllAsync())
                             .Select(d => d.SolarRequestId)
                             .ToHashSet();
                all = all.Where(r => (int)r.CurrentStage > (int)stage || docIds.Contains(r.Id)).ToList();
            }
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

            // DCR pending queue: sirf wo requests dikhao jinke liye USER ne
            // apna DCR upload kar diya hai (DCRDocument row Pending status mein).
            // Installation ke baad stage DCRUpdate par aate hi row pending mein
            // nahi aani chahiye — jab tak user upload nahi karta, admin ke paas
            // verify karne ko kuch hai hi nahi. (Same pattern as PM Surya pending.)
            if (string.Equals(op, "dcr", StringComparison.OrdinalIgnoreCase))
            {
                var pendingDocIds = (await _uow.DCRDocuments.FindAsync(
                                        d => d.ApprovalStatus == ApprovalStatus.Pending))
                                    .Select(d => d.SolarRequestId)
                                    .ToHashSet();
                all = all.Where(r => pendingDocIds.Contains(r.Id)).ToList();
            }
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

                // Material Dispatch is where the INC installer gets assigned, and that
                // assignment is what makes a commission payout possible. If the plan the
                // project sits on has no commission amount configured, no payout can ever
                // be generated — surface it while the admin is assigning, rather than
                // letting it fail silently in the INC Commission report later.
                // Exposed as a TYPED dictionary so the view can cast it and build the
                // JSON island itself (same pattern as InstallationDetails).
                var planIds = rows.Where(r => r.SolarProjectId.HasValue)
                                  .Select(r => r.SolarProjectId!.Value).Distinct().ToHashSet();
                ViewBag.PlansById = planIds.Any()
                    ? (await _uow.SolarProjects.FindAsync(p => planIds.Contains(p.Id))).ToDictionary(p => p.Id)
                    : new Dictionary<int, SolarProject>();
                break;
            case "installation":
                var installs = (await _uow.Installations.FindAsync(i => ids.Contains(i.SolarRequestId)))
                               .GroupBy(i => i.SolarRequestId)
                               .ToDictionary(g => g.Key, g => g.OrderByDescending(i => i.CreatedAt).First());
                // Task 17 (same latent bug): attach the installer worker too.
                await AttachWorkersAsync(installs.Values.Select(i => (i.AssignedWorkerId, (Action<Worker>)(w => i.AssignedWorker = w))));
                ViewBag.InstallationDetails = installs;

                // Spec: "material dispatch mein jo person assign kiya, installation
                // mein wahi pre-selected aana chahiye" — Installation queue par
                // MaterialDispatch ki assignment bhi load karo taaki modal use
                // pre-select kar sake aur list mein naam dikh sake (installation
                // row banne se pehle bhi).
                var dispatchAssign = (await _uow.MaterialDispatches.FindAsync(m => ids.Contains(m.SolarRequestId)))
                                     .GroupBy(m => m.SolarRequestId)
                                     .ToDictionary(g => g.Key, g => g.OrderByDescending(m => m.CreatedAt).First());
                await AttachWorkersAsync(dispatchAssign.Values.Select(m => (m.AssignedWorkerId, (Action<Worker>)(w => m.AssignedWorker = w))));
                ViewBag.DispatchAssignments = dispatchAssign;

                // Point 11: the INC's mark-installed photos (up to 30) have to be
                // visible to the admin, who approves or rejects the whole batch.
                // Keyed by SolarRequestId so the queue row can show them without
                // knowing the Installation id.
                var installIds = installs.Values.Select(i => i.Id).ToHashSet();
                var photos = installIds.Count == 0
                    ? new List<InstallationPhoto>()
                    : (await _uow.InstallationPhotos.FindAsync(p => installIds.Contains(p.InstallationId))).ToList();
                ViewBag.InstallationPhotos = photos
                    .GroupBy(p => p.SolarRequestId)
                    .ToDictionary(g => g.Key, g => g.OrderBy(p => p.Id).ToList());
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

    /// <summary>
    /// Has the site-survey leg finished for this request? Point 3 runs Meter
    /// Dispatch and Site Survey side by side, so each leg asks this about the
    /// other before deciding whether the project can move to Material Dispatch.
    /// </summary>
    private async Task<bool> IsSiteSurveyApprovedAsync(int requestId) =>
        (await _uow.SiteSurveys.FindAsync(s => s.SolarRequestId == requestId))
            .Any(s => s.ApprovalStatus == ApprovalStatus.Approved || s.IsCompleted);

    /// <summary>Has the meter been dispatched for this request? Counterpart of the above.</summary>
    private async Task<bool> IsMeterDispatchedAsync(int requestId) =>
        (await _uow.MeterDispatches.FindAsync(m => m.SolarRequestId == requestId))
            .Any(m => m.IsDispatched);

    // --- Meter Dispatch ---
    // Spec flow: PM Surya Ghar → (Meter Dispatch ∥ Site Survey) → Material Dispatch.
    // After admin approves PM Surya Ghar the project's CurrentStage becomes
    // MeterDispatch and BOTH queues open; whichever finishes last moves it on.
    public async Task<IActionResult> MeterDispatch(string? state, string? city, string? filter)
    {
        var f = (filter ?? "all").ToLowerInvariant();
        var showHistory = f == "all";
        ViewBag.Filter = f;

        // Point 3: the meter can be dispatched at ANY time - the site survey is
        // what moves the project forward. So this queue cannot key off the project
        // sitting at the MeterDispatch stage: once the survey pushes it to Material
        // Dispatch the row would vanish and the meter could never be recorded.
        //
        // Start from PM Surya onwards and filter on whether a meter has actually
        // been dispatched. Pending = no meter yet, whatever stage the project is at.
        var reached = (await _uow.SolarRequests.FindAsync(r =>
                          r.CurrentStage == ProjectStatus.MeterDispatch ||
                          r.CurrentStage == ProjectStatus.SiteSurvey ||
                          r.CurrentStage == ProjectStatus.MaterialDispatch ||
                          r.CurrentStage == ProjectStatus.Installation ||
                          r.CurrentStage == ProjectStatus.DCRUpdate ||
                          r.CurrentStage == ProjectStatus.Completed)).ToList();

        var ids = reached.Select(r => r.Id).ToHashSet();
        var dispatched = ids.Count == 0
            ? new HashSet<int>()
            : (await _uow.MeterDispatches.FindAsync(m => ids.Contains(m.SolarRequestId)))
              .Where(m => m.IsDispatched)
              .Select(m => m.SolarRequestId)
              .ToHashSet();

        var requests = f switch
        {
            "pending" => reached.Where(r => !dispatched.Contains(r.Id)),
            "approved" => reached.Where(r => dispatched.Contains(r.Id)),
            _ => reached
        };

        // City / state filters, same as the shared helper applies elsewhere.
        if (!string.IsNullOrWhiteSpace(state))
            requests = requests.Where(r => string.Equals(r.State?.Trim(), state.Trim(), StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(city))
            requests = requests.Where(r => (r.City ?? "").Contains(city.Trim(), StringComparison.OrdinalIgnoreCase));

        var list = requests.OrderByDescending(r => r.CreatedAt).ToList();

        await PopulateFilterViewBags(state, city, list);
        ViewBag.Title = "Meter Dispatch";
        ViewBag.Op = "meter";
        await PopulateOperationDetailsAsync("meter", list);
        return View("OperationsList", list);
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

            // Point 3: the SITE SURVEY moves the project on; the meter can be
            // dispatched at any time, before or after that. So this leg records the
            // meter and only nudges the stage when the project is still sitting at
            // MeterDispatch AND the survey is already done. A project that has
            // moved ahead is left exactly where it is - dragging it backwards to
            // Material Dispatch would undo real progress.
            var req = await _uow.SolarRequests.GetByIdAsync(requestId);
            var stageMoved = false;

            if (req != null &&
                req.CurrentStage == ProjectStatus.MeterDispatch &&
                await IsSiteSurveyApprovedAsync(requestId))
            {
                var stageResult = await _requestService.UpdateStageAsync(new UpdateSolarRequestStatusDto
                {
                    Id = requestId,
                    NewStage = ProjectStatus.MaterialDispatch,
                    Notes = $"Meter {meterNumber} dispatched on {dispatch.DispatchDate:dd/MM/yyyy}. Site survey already approved."
                }, _userManager.GetUserId(User)!);

                if (!stageResult.IsSuccess)
                    return Json(new { success = false, message = $"Stage update failed: {stageResult.Message ?? string.Join("; ", stageResult.Errors)}" });

                stageMoved = true;
            }

            return Json(new
            {
                success = true,
                message = stageMoved
                    ? $"Meter {meterNumber} dispatched. Site survey was already approved - project moved to Material Dispatch."
                    : $"Meter {meterNumber} dispatched."
            });
        }
        catch (Exception ex)
        {
            var detail = ex.InnerException?.Message ?? ex.Message;
            return Json(new { success = false, message = $"Meter dispatch failed: {detail}" });
        }
    }

    // ═══ Material Dispatch — now TWO steps (change request point 6) ═══════
    // "Material Dispatch — 2 Step. Alag 2 menu: (1) Prepare for Dispatch
    //  (2) Final Dispatch."
    //
    // Step 1 records what is going out and who will install it, and leaves the
    // project where it is. Step 2 actually sends it and advances the project to
    // Installation. One MaterialDispatch row carries both milestones
    // (IsPrepared, then IsDispatched), so nothing about the existing history,
    // reports or installer assignment changes shape.

    // Old single-screen entry point. Kept so existing bookmarks and links still
    // land somewhere sensible — it now opens step 1.
    public IActionResult MaterialDispatch(string? state, string? city, string? filter) =>
        RedirectToAction(nameof(PrepareDispatch), new { state, city, filter });

    /// <summary>Latest MaterialDispatch row per request, for the two queues below.</summary>
    private async Task<Dictionary<int, MaterialDispatch>> LatestDispatchesAsync(IEnumerable<int> requestIds)
    {
        var ids = requestIds.ToHashSet();
        if (ids.Count == 0) return new Dictionary<int, MaterialDispatch>();

        return (await _uow.MaterialDispatches.FindAsync(m => ids.Contains(m.SolarRequestId)))
               .GroupBy(m => m.SolarRequestId)
               .ToDictionary(g => g.Key, g => g.OrderByDescending(m => m.CreatedAt).First());
    }

    // --- Step 1: Prepare for Dispatch ---
    public async Task<IActionResult> PrepareDispatch(string? state, string? city, string? filter)
    {
        var f = (filter ?? "all").ToLowerInvariant();
        var showHistory = f == "all";
        ViewBag.Filter = f;

        var requests = (await FilterAsync(ProjectStatus.MaterialDispatch, state, city,
                                          showHistory: showHistory, filterMode: f, op: "material")).ToList();

        // Pending = at this stage and not prepared yet.
        // Approved = preparation done (whether or not it has gone out).
        var latest = await LatestDispatchesAsync(requests.Select(r => r.Id));
        requests = f switch
        {
            "pending" => requests.Where(r => !(latest.TryGetValue(r.Id, out var d) && d.IsPrepared)).ToList(),
            "approved" => requests.Where(r => latest.TryGetValue(r.Id, out var d) && d.IsPrepared).ToList(),
            _ => requests
        };

        await PopulateFilterViewBags(state, city, requests);
        ViewBag.Title = "Prepare for Dispatch";
        ViewBag.Op = "prepare";
        await PopulateOperationDetailsAsync("material", requests);
        return View("OperationsList", requests);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SubmitPrepareDispatch(int requestId, string? materialDetails,
        string? vehicleDetails, string? prepareRemark, int? workerId, IFormFile? dispatchDoc)
    {
        try
        {
            // Installer assignment is mandatory at preparation time — the same rule
            // the old single-step dispatch enforced, just moved one step earlier so
            // the INC knows about the job before the material leaves.
            if (!workerId.HasValue || workerId.Value <= 0)
                return Json(new { success = false, message = "Please assign an installer before preparing the dispatch." });

            var commissionBlock = await BlockIncWithoutCommissionAsync(requestId, workerId.Value);
            if (commissionBlock != null) return Json(new { success = false, message = commissionBlock });

            string? docPath = null;
            if (dispatchDoc != null)
            {
                var (ok, path, err) = await _fileUploadService.UploadAsync(dispatchDoc, "dispatch/material");
                if (!ok) return Json(new { success = false, message = $"Document upload failed: {err}" });
                docPath = path;
            }

            // Upsert: re-opening Prepare on the same request corrects the existing
            // row rather than stacking duplicates that the Final queue would then
            // show twice.
            var dispatch = (await _uow.MaterialDispatches.FindAsync(m => m.SolarRequestId == requestId))
                           .OrderByDescending(m => m.CreatedAt)
                           .FirstOrDefault();
            var isNew = dispatch == null;
            dispatch ??= new MaterialDispatch { SolarRequestId = requestId };

            if (dispatch.IsDispatched)
                return Json(new { success = false, message = "This material has already been finally dispatched." });

            dispatch.MaterialDetails = materialDetails;
            dispatch.VehicleDetails = vehicleDetails;
            dispatch.PrepareRemark = prepareRemark;
            dispatch.AssignedWorkerId = workerId;
            if (docPath != null) dispatch.DispatchDocumentPath = docPath;
            dispatch.IsPrepared = true;
            dispatch.PreparedAt = DateTime.UtcNow;
            dispatch.PreparedBy = _userManager.GetUserId(User);

            if (isNew) await _uow.MaterialDispatches.AddAsync(dispatch);
            else _uow.MaterialDispatches.Update(dispatch);
            await _uow.SaveChangesAsync();

            await _activity.LogAsync(_userManager.GetUserId(User) ?? "system",
                "MaterialDispatch.Prepare", "SolarRequest", requestId.ToString(),
                $"Prepared for dispatch. Installer #{workerId}. {materialDetails}".Trim(),
                HttpContext.Connection.RemoteIpAddress?.ToString());

            // Deliberately no stage change — the project stays at Material Dispatch
            // until step 2 sends it.
            return Json(new
            {
                success = true,
                message = "Prepared for dispatch. It now appears in the Final Dispatch queue."
            });
        }
        catch (Exception ex)
        {
            var detail = ex.InnerException?.Message ?? ex.Message;
            return Json(new { success = false, message = $"Prepare failed: {detail}" });
        }
    }

    // --- Step 2: Final Dispatch ---
    public async Task<IActionResult> FinalDispatch(string? state, string? city, string? filter)
    {
        var f = (filter ?? "all").ToLowerInvariant();
        var showHistory = f == "all";
        ViewBag.Filter = f;

        var requests = (await FilterAsync(ProjectStatus.MaterialDispatch, state, city,
                                          showHistory: showHistory, filterMode: f, op: "material")).ToList();

        // Pending = prepared but not yet sent. Nothing reaches this queue until
        // step 1 has been done, which is the whole point of splitting the menu.
        var latest = await LatestDispatchesAsync(requests.Select(r => r.Id));
        requests = f switch
        {
            "pending" => requests.Where(r => latest.TryGetValue(r.Id, out var d) && d.IsPrepared && !d.IsDispatched).ToList(),
            "approved" => requests.Where(r => latest.TryGetValue(r.Id, out var d) && d.IsDispatched).ToList(),
            _ => requests
        };

        await PopulateFilterViewBags(state, city, requests);
        ViewBag.Title = "Final Dispatch";
        ViewBag.Op = "final";
        await PopulateOperationDetailsAsync("material", requests);
        return View("OperationsList", requests);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SubmitFinalDispatch(int requestId, DateTime? dispatchDate,
        string? remark, IFormFile? dispatchDoc)
    {
        try
        {
            var dispatch = (await _uow.MaterialDispatches.FindAsync(m => m.SolarRequestId == requestId))
                           .OrderByDescending(m => m.CreatedAt)
                           .FirstOrDefault();

            if (dispatch == null || !dispatch.IsPrepared)
                return Json(new { success = false, message = "Prepare this dispatch first — Final Dispatch only handles prepared rows." });

            if (dispatch.IsDispatched)
                return Json(new { success = false, message = "This material has already been dispatched." });

            // The installer chosen at preparation is what makes the INC payout
            // possible, so re-check it here: the plan could have been changed
            // between the two steps.
            if (!dispatch.AssignedWorkerId.HasValue)
                return Json(new { success = false, message = "No installer is assigned. Re-open Prepare for Dispatch and assign one." });

            var commissionBlock = await BlockIncWithoutCommissionAsync(requestId, dispatch.AssignedWorkerId.Value);
            if (commissionBlock != null) return Json(new { success = false, message = commissionBlock });

            if (dispatchDoc != null)
            {
                var (ok, path, err) = await _fileUploadService.UploadAsync(dispatchDoc, "dispatch/material");
                if (!ok) return Json(new { success = false, message = $"Document upload failed: {err}" });
                dispatch.DispatchDocumentPath = path;
            }

            dispatch.DispatchDate = dispatchDate ?? DateTime.UtcNow;
            if (!string.IsNullOrWhiteSpace(remark)) dispatch.Remark = remark;
            dispatch.IsDispatched = true;
            dispatch.DispatchedBy = _userManager.GetUserId(User);
            _uow.MaterialDispatches.Update(dispatch);
            await _uow.SaveChangesAsync();

            var stageResult = await _requestService.UpdateStageAsync(new UpdateSolarRequestStatusDto
            {
                Id = requestId,
                NewStage = ProjectStatus.Installation,
                Notes = $"Material dispatched on {dispatch.DispatchDate:dd/MM/yyyy}"
            }, _userManager.GetUserId(User)!);

            if (!stageResult.IsSuccess)
                return Json(new { success = false, message = $"Stage update failed: {stageResult.Message ?? string.Join("; ", stageResult.Errors)}" });

            await _activity.LogAsync(_userManager.GetUserId(User) ?? "system",
                "MaterialDispatch.Final", "SolarRequest", requestId.ToString(),
                $"Material finally dispatched on {dispatch.DispatchDate:dd/MM/yyyy}. Project moved to Installation.",
                HttpContext.Connection.RemoteIpAddress?.ToString());

            return Json(new { success = true, message = "Material dispatched. Project moved to Installation." });
        }
        catch (Exception ex)
        {
            var detail = ex.InnerException?.Message ?? ex.Message;
            return Json(new { success = false, message = $"Final dispatch failed: {detail}" });
        }
    }

    /// <summary>
    /// An INC installer earns the plan's commission; a JOB worker is salaried and
    /// needs none. So block only the INC case when the plan has no amount
    /// configured — otherwise the assignment is created but can never pay out.
    /// Returns the error text, or null when the assignment is fine.
    /// </summary>
    private async Task<string?> BlockIncWithoutCommissionAsync(int requestId, int workerId)
    {
        var assignee = await _uow.Workers.GetByIdAsync(workerId);
        if (assignee == null || assignee.Type != WorkerType.INC) return null;

        var reqForPlan = await _uow.SolarRequests.GetByIdAsync(requestId);
        SolarProject? plan = reqForPlan?.SolarProjectId is int pid
            ? await _uow.SolarProjects.GetByIdAsync(pid)
            : null;

        if (plan?.IncCommissionAmount > 0m) return null;

        var planLabel = plan?.Name ?? reqForPlan?.SelectedPlan;
        return "No commission is set for the " +
               (string.IsNullOrWhiteSpace(planLabel) ? "selected" : "\"" + planLabel + "\"") +
               " plan. Add the commission amount in INC Commission before dispatching to an INC installer.";
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SubmitMaterialDispatch(int requestId, string? materialDetails,
        DateTime? dispatchDate, string? vehicleDetails, string? remark, int? workerId, IFormFile? dispatchDoc)
    {
        try
        {
            // Installer assignment is mandatory (also enforced on the client).
            if (!workerId.HasValue || workerId.Value <= 0)
                return Json(new { success = false, message = "Please assign an installer before dispatching." });

                // An INC installer earns the plan's commission; a JOB worker is salaried
                // and needs none. So block only the INC case when the plan has no amount
                // configured — otherwise the assignment is created but can never pay out.
                // The modal blocks this too; this is the authoritative check.
                var assignee = await _uow.Workers.GetByIdAsync(workerId.Value);
                if (assignee != null && assignee.Type == WorkerType.INC)
                {
                    var reqForPlan = await _uow.SolarRequests.GetByIdAsync(requestId);
                    SolarProject? plan = reqForPlan?.SolarProjectId is int pid
                        ? await _uow.SolarProjects.GetByIdAsync(pid)
                        : null;
                    if (!(plan?.IncCommissionAmount > 0m))
                    {
                        var planLabel = plan?.Name ?? reqForPlan?.SelectedPlan;
                        return Json(new
                        {
                            success = false,
                            message = $"No commission is set for the " +
                                      (string.IsNullOrWhiteSpace(planLabel) ? "selected" : "\"" + planLabel + "\"") +
                                      " plan. Add the commission amount in INC Commission before dispatching to an INC installer."
                        });
                    }
                }

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
                // Point 6 split this into Prepare + Final. This one-shot handler is
                // no longer reachable from the menu (MaterialDispatch redirects to
                // PrepareDispatch) but is kept for old links; stamping IsPrepared
                // means a row created this way never reappears in the Prepare queue.
                IsPrepared = true,
                PreparedAt = DateTime.UtcNow,
                PreparedBy = _userManager.GetUserId(User),
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
        var f = (filter ?? "all").ToLowerInvariant();
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
            // Spec: installer Material Dispatch mein pehle hi assign ho chuka hai, so
            // the modal opens pre-selected. If the client sends nothing anyway, fall
            // back to that dispatch assignment instead of rejecting the submit.
            if (!workerId.HasValue || workerId.Value <= 0)
            {
                workerId = (await _uow.MaterialDispatches.FindAsync(m => m.SolarRequestId == requestId))
                           .OrderByDescending(m => m.CreatedAt)
                           .FirstOrDefault()?.AssignedWorkerId;
            }

            // Installer assignment is mandatory (also enforced on the client).
            if (!workerId.HasValue || workerId.Value <= 0)
                return Json(new { success = false, message = "Please assign an installer before submitting." });

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

    // Admin ki Installation screen ab sirf do kaam karti hai (spec):
    //   1. Remark likhna
    //   2. Material Dispatch se aaya hua installer check karna
    // Actual "Mark Installation" INC panel (SolarPanelInstaller area, alag app)
    // par chala gaya hai. Ye action installation row ko *assigned* state mein
    // banata/update karta hai — complete NAHI karta aur stage aage nahi badhata,
    // taaki INC worker use apne panel mein utha sake.
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveInstallationRemark(int requestId, string? remark)
    {
        try
        {
            var installation = (await _uow.Installations.FindAsync(i => i.SolarRequestId == requestId))
                               .OrderByDescending(i => i.CreatedAt)
                               .FirstOrDefault();

            // Installer hamesha Material Dispatch wali assignment se aata hai —
            // admin yahan sirf verify karta hai, dobara select nahi karta.
            var dispatchWorkerId = (await _uow.MaterialDispatches.FindAsync(m => m.SolarRequestId == requestId))
                                   .OrderByDescending(m => m.CreatedAt)
                                   .FirstOrDefault()?.AssignedWorkerId;

            if (installation == null)
            {
                installation = new Installation
                {
                    SolarRequestId = requestId,
                    AssignedWorkerId = dispatchWorkerId,
                    Remark = remark,
                    IsCompleted = false
                };
                await _uow.Installations.AddAsync(installation);
            }
            else
            {
                installation.Remark = remark;
                installation.AssignedWorkerId ??= dispatchWorkerId;
                _uow.Installations.Update(installation);
            }

            await _uow.SaveChangesAsync();
            return Json(new { success = true, message = "Remark saved. Installation is with the INC panel." });
        }
        catch (Exception ex)
        {
            var detail = ex.InnerException?.Message ?? ex.Message;
            return Json(new { success = false, message = $"Could not save remark: {detail}" });
        }
    }

    // Change the installer (the "INC change" option).
    //
    // Point 10 fix: this used to require an existing Installation row, so on the
    // dispatch queues — and on an Installation that had only inherited its
    // installer from the dispatch — the option simply never appeared. It now also
    // accepts a MaterialDispatch id and updates whichever record actually holds
    // the assignment, keeping both in step when both exist.
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ChangeInstaller(int workerId, string? note,
        int installationId = 0, int dispatchId = 0, int requestId = 0)
    {
        try
        {
            if (workerId <= 0)
                return Json(new { success = false, message = "Please choose an installer." });

            if (installationId <= 0 && dispatchId <= 0 && requestId <= 0)
                return Json(new { success = false, message = "Nothing to update — no request was supplied." });

            var worker = await _uow.Workers.GetByIdAsync(workerId);
            if (worker == null)
                return Json(new { success = false, message = "Selected worker not found." });

            var installation = installationId > 0
                ? await _uow.Installations.GetByIdAsync(installationId)
                : null;

            var dispatch = dispatchId > 0
                ? await _uow.MaterialDispatches.GetByIdAsync(dispatchId)
                : null;

            // Point 10: the queues now offer Change on EVERY row, including ones
            // that have neither record yet — previously the button only appeared
            // once some other action had already created one, which is exactly
            // why "INC change ka option nahi aa raha" on a fresh row. Resolve
            // from the request itself, and start the dispatch record if the
            // assignment has nowhere to live yet.
            if (installation == null && dispatch == null && requestId > 0)
            {
                installation = (await _uow.Installations.FindAsync(i => i.SolarRequestId == requestId))
                               .OrderByDescending(i => i.CreatedAt).FirstOrDefault();

                dispatch = (await _uow.MaterialDispatches.FindAsync(m => m.SolarRequestId == requestId))
                           .OrderByDescending(m => m.CreatedAt).FirstOrDefault();

                if (installation == null && dispatch == null)
                {
                    // IsPrepared stays false, so Prepare for Dispatch still shows
                    // this project as outstanding work — only the installer is set.
                    dispatch = new MaterialDispatch { SolarRequestId = requestId };
                    await _uow.MaterialDispatches.AddAsync(dispatch);
                    await _uow.SaveChangesAsync();
                }
            }

            if (installation == null && dispatch == null)
                return Json(new { success = false, message = "Installation / dispatch record not found." });

            // The INC/no-commission rule applies to a change just as much as to
            // the original assignment — otherwise a change could quietly move the
            // job to an installer who can never be paid for it.
            var targetRequestId = installation?.SolarRequestId ?? dispatch!.SolarRequestId;
            var commissionBlock = await BlockIncWithoutCommissionAsync(targetRequestId, workerId);
            if (commissionBlock != null)
                return Json(new { success = false, message = commissionBlock });

            if (dispatch != null)
            {
                // The dispatch assignment is what Installation inherits from, so it
                // has to move too or the change would silently revert.
                dispatch.AssignedWorkerId = workerId;
                _uow.MaterialDispatches.Update(dispatch);
            }

            if (installation == null)
            {
                await _uow.SaveChangesAsync();

                await _activity.LogAsync(_userManager.GetUserId(User) ?? "system",
                    "Installer.Change", "SolarRequest", targetRequestId.ToString(),
                    $"Installer set to {worker.Name} on the material dispatch." +
                    (string.IsNullOrWhiteSpace(note) ? "" : $" Note: {note}"),
                    HttpContext.Connection.RemoteIpAddress?.ToString());

                return Json(new { success = true, message = $"Installer changed to {worker.Name}." });
            }

            installationId = installation.Id;
            installation.AssignedWorkerId = workerId;
            _uow.Installations.Update(installation);

            // Keep the assignment log in step: update the existing row if there is
            // one, else create it (older installations may predate the log).
            var assignment = (await _uow.WorkerAssignments.FindAsync(a => a.InstallationId == installationId))
                             .OrderByDescending(a => a.Id)
                             .FirstOrDefault();
            if (assignment == null)
            {
                await _uow.WorkerAssignments.AddAsync(new WorkerAssignment
                {
                    InstallationId = installationId,
                    WorkerId = workerId,
                    AssignedByUserId = _userManager.GetUserId(User) ?? "system",
                    AssignedDate = DateTime.UtcNow,
                    Notes = note
                });
            }
            else
            {
                assignment.WorkerId = workerId;
                assignment.AssignedByUserId = _userManager.GetUserId(User) ?? "system";
                assignment.AssignedDate = DateTime.UtcNow;
                if (!string.IsNullOrWhiteSpace(note)) assignment.Notes = note;
                _uow.WorkerAssignments.Update(assignment);
            }

            await _uow.SaveChangesAsync();

            await _activity.LogAsync(_userManager.GetUserId(User) ?? "system",
                "Installer.Change", "SolarRequest", targetRequestId.ToString(),
                $"Installer changed to {worker.Name}." +
                (string.IsNullOrWhiteSpace(note) ? "" : $" Note: {note}"),
                HttpContext.Connection.RemoteIpAddress?.ToString());

            return Json(new { success = true, message = $"Installer changed to {worker.Name}." });
        }
        catch (Exception ex)
        {
            var detail = ex.InnerException?.Message ?? ex.Message;
            return Json(new { success = false, message = $"Installer change failed: {detail}" });
        }
    }

    // ═══ Installation photos (change request point 11) ════════════════════
    // "INC — Mark Installed → multiple photo upload upto 30 photo. Ye admin ko
    //  show hona chahiye. Admin se approve hone par credit hona chahiye. Reject
    //  hone par INC wapas upload karega."
    //
    // The INC uploads the batch from the installer panel; the admin approves or
    // rejects the WHOLE batch here.
    //
    // Approval is what ENTITLES the INC to the commission, but this app does not
    // pay it. The installer panel owns that: its sweep picks up every approved,
    // not-yet-credited installation and posts it, keying idempotency on
    // IncCommissionLedger.SolarRequestId. If the admin also wrote the wallet
    // ledger directly, that check would not see the payment and the same project
    // would be credited a second time. So the handshake is exactly one flag:
    // admin sets ApprovalStatus, the installer panel sets CommissionCredited.

    // ═══ Installation photo approval report (change request point 11) ═════
    // "Ye admin ko show hona chahiye. Admin se approve hone par credit hona
    //  chahiye. Reject hone par INC wapas update karega."
    //
    // Deliberately NOT filtered by project stage. Marking an installation
    // complete advances the project to DCR / Completed, so by the time the photos
    // need a decision the row has already left the Installation queue - which is
    // exactly how a batch could sit unapproved forever and the INC never get paid.
    // This report keys off the photo batch itself, so nothing can fall out of it.
    public async Task<IActionResult> InstallationApprovals(string? status)
    {
        // Default is the FULL report - every batch, whatever its state. Pending
        // is one tab away, but an admin opening this menu should first see the
        // whole picture rather than a filtered slice.
        var f = (status ?? "all").ToLowerInvariant();

        // Only installations that actually have photos - there is nothing to
        // decide on the rest.
        var photos = (await _uow.InstallationPhotos.GetAllAsync()).ToList();
        var byInstallation = photos.GroupBy(p => p.InstallationId)
                                   .ToDictionary(g => g.Key, g => g.OrderBy(p => p.Id).ToList());
        if (byInstallation.Count == 0)
        {
            ViewBag.Status = f;
            ViewBag.Photos = byInstallation;
            ViewBag.Requests = new Dictionary<int, SolarRequest>();
            ViewBag.Workers = new Dictionary<int, Worker>();
            ViewBag.Counts = new Dictionary<string, int>
            {
                ["pending"] = 0, ["approved"] = 0, ["rejected"] = 0
            };
            ViewBag.Title = "Installation Approval";
            return View(new List<Installation>());
        }

        var ids = byInstallation.Keys.ToHashSet();
        var installs = (await _uow.Installations.FindAsync(i => ids.Contains(i.Id))).ToList();

        // Counts come from the full set, so the tab badges stay right whichever
        // tab is open.
        ViewBag.Counts = new Dictionary<string, int>
        {
            ["pending"]  = installs.Count(i => i.ApprovalStatus == ApprovalStatus.Pending),
            ["approved"] = installs.Count(i => i.ApprovalStatus == ApprovalStatus.Approved),
            ["rejected"] = installs.Count(i => i.ApprovalStatus == ApprovalStatus.Rejected)
        };

        var rows = f switch
        {
            "approved" => installs.Where(i => i.ApprovalStatus == ApprovalStatus.Approved),
            "rejected" => installs.Where(i => i.ApprovalStatus == ApprovalStatus.Rejected),
            "all"      => installs,
            _          => installs.Where(i => i.ApprovalStatus == ApprovalStatus.Pending)
        };

        // Oldest submission first: a batch that has been waiting longest is the
        // one holding up an installer's money.
        var list = rows.OrderBy(i => i.SubmittedAt ?? i.CompletedAt ?? i.CreatedAt).ToList();

        var reqIds = list.Select(i => i.SolarRequestId).ToHashSet();
        ViewBag.Requests = (await _uow.SolarRequests.FindAsync(r => reqIds.Contains(r.Id)))
                           .ToDictionary(r => r.Id);

        // Installer falls back to the dispatch assignment, same as everywhere else.
        var workerIds = list.Where(i => i.AssignedWorkerId.HasValue)
                            .Select(i => i.AssignedWorkerId!.Value).ToHashSet();
        foreach (var d in await _uow.MaterialDispatches.FindAsync(m => reqIds.Contains(m.SolarRequestId)))
            if (d.AssignedWorkerId.HasValue) workerIds.Add(d.AssignedWorkerId.Value);

        ViewBag.Workers = workerIds.Count == 0
            ? new Dictionary<int, Worker>()
            : (await _uow.Workers.FindAsync(w => workerIds.Contains(w.Id))).ToDictionary(w => w.Id);

        ViewBag.DispatchWorker = (await _uow.MaterialDispatches.FindAsync(m => reqIds.Contains(m.SolarRequestId)))
            .Where(m => m.AssignedWorkerId.HasValue)
            .GroupBy(m => m.SolarRequestId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(m => m.CreatedAt).First().AssignedWorkerId!.Value);

        ViewBag.Photos = byInstallation;
        ViewBag.Status = f;
        ViewBag.Title = "Installation Approval";
        return View(list);
    }

    /// <summary>Maximum photos in one mark-installed batch, per the spec.</summary>
    public const int MaxInstallationPhotos = 30;

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ApproveInstallationPhotos(int installationId, string? remark)
    {
        try
        {
            var installation = await _uow.Installations.GetByIdAsync(installationId);
            if (installation == null)
                return Json(new { success = false, message = "Installation record not found." });

            var photos = (await _uow.InstallationPhotos.FindAsync(p => p.InstallationId == installationId)).ToList();
            if (photos.Count == 0)
                return Json(new { success = false, message = "There are no photos to approve on this installation yet." });

            if (installation.ApprovalStatus == ApprovalStatus.Approved)
                return Json(new { success = false, message = "These photos are already approved." });

            installation.ApprovalStatus = ApprovalStatus.Approved;
            installation.RejectionReason = null;      // clear any earlier rejection
            installation.Notes = string.IsNullOrWhiteSpace(remark)
                ? installation.Notes
                : $"{installation.Notes}\n[PHOTOS APPROVED] {remark}".Trim();
            installation.ReviewedBy = _userManager.GetUserId(User);
            installation.ReviewedAt = DateTime.UtcNow;
            _uow.Installations.Update(installation);
            await _uow.SaveChangesAsync();

            // "Admin se approve hone par credit hona chahiye" - so pay now rather
            // than waiting for the installer to open their panel. A failure here
            // must not undo the approval: the installer panel's catch-up sweep
            // will post it, and both paths share one idempotency key so only one
            // of them can ever succeed.
            var credit = await CreditApprovedInstallationAsync(installation);

            await _activity.LogAsync(_userManager.GetUserId(User) ?? "system",
                "Installation.ApprovePhotos", "Installation", installationId.ToString(),
                $"Approved {photos.Count} installation photo(s). {credit.Message}",
                HttpContext.Connection.RemoteIpAddress?.ToString());

            return Json(new
            {
                success = true,
                message = $"{photos.Count} photo(s) approved. {credit.Message}"
            });
        }
        catch (Exception ex)
        {
            var detail = ex.InnerException?.Message ?? ex.Message;
            return Json(new { success = false, message = $"Photo approval failed: {detail}" });
        }
    }

    /// <summary>
    /// Pays the INC for an installation whose photos the admin just approved.
    ///
    /// Never throws: a commission problem must not roll back an approval the
    /// admin already made and already saw succeed. If the post fails, the row is
    /// simply left with CommissionCredited = false and the installer panel's
    /// catch-up sweep retries it. Both paths key idempotency on the same
    /// IncCommissionLedger row, so the retry can never pay twice.
    /// </summary>
    private async Task<IncCommissionCreditResult> CreditApprovedInstallationAsync(Installation installation)
    {
        try
        {
            if (installation.CommissionCredited)
                return new IncCommissionCreditResult { Message = "Commission was already credited for this project." };

            // Fall back to the dispatch assignment: the installer is chosen at
            // Prepare-for-Dispatch time and an Installation row may never have
            // been given one of its own.
            var workerId = installation.AssignedWorkerId
                           ?? (await _uow.MaterialDispatches.FindAsync(m => m.SolarRequestId == installation.SolarRequestId))
                              .OrderByDescending(m => m.CreatedAt).FirstOrDefault()?.AssignedWorkerId;

            if (!workerId.HasValue)
                return new IncCommissionCreditResult { Message = "No installer is assigned, so no commission was credited." };

            var me = _userManager.GetUserId(User) ?? "admin";
            var result = await _incCommission.CreditForRequestAsync(installation.SolarRequestId, workerId.Value, me);

            // Stamp the flag whenever the money is confirmed present - whether we
            // posted it or found it already there - so the installer panel's
            // sweep stops reconsidering this row.
            if (result.Credited || result.Message.Contains("already been credited"))
            {
                installation.CommissionCredited = true;
                _uow.Installations.Update(installation);
                await _uow.SaveChangesAsync();
            }

            return result;
        }
        catch (Exception ex)
        {
            return new IncCommissionCreditResult
            {
                Message = "The approval was saved, but the commission could not be credited right now " +
                          $"({ex.InnerException?.Message ?? ex.Message}). The installer's panel will post it automatically."
            };
        }
    }
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RejectInstallationPhotos(int installationId, string reason)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(reason))
                return Json(new { success = false, message = "A reason is required so the INC knows what to re-shoot." });

            var installation = await _uow.Installations.GetByIdAsync(installationId);
            if (installation == null)
                return Json(new { success = false, message = "Installation record not found." });

            // Rejecting after the money has gone out would leave the INC paid for
            // work that was sent back. Once the installer panel has credited it,
            // the batch is final.
            if (installation.CommissionCredited)
                return Json(new
                {
                    success = false,
                    message = "These photos were already approved and the commission has been credited, so they cannot be rejected now."
                });

            installation.ApprovalStatus = ApprovalStatus.Rejected;
            installation.RejectionReason = reason;
            installation.ReviewedBy = _userManager.GetUserId(User);
            installation.ReviewedAt = DateTime.UtcNow;
            _uow.Installations.Update(installation);
            await _uow.SaveChangesAsync();

            await _activity.LogAsync(_userManager.GetUserId(User) ?? "system",
                "Installation.RejectPhotos", "Installation", installationId.ToString(),
                $"Rejected installation photos. Reason: {reason}",
                HttpContext.Connection.RemoteIpAddress?.ToString());

            return Json(new
            {
                success = true,
                message = "Photos rejected. The INC can upload a fresh set from their panel."
            });
        }
        catch (Exception ex)
        {
            var detail = ex.InnerException?.Message ?? ex.Message;
            return Json(new { success = false, message = $"Photo rejection failed: {detail}" });
        }
    }


    // --- DCR Update (Domestic only) ---
    public async Task<IActionResult> DCRUpdate(string? state, string? city, string? filter)
    {
        var f = (filter ?? "all").ToLowerInvariant();
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
