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
    //   rejected → only ever holds legacy rows now: DCR used to be uploaded by the
    //              user and approved/rejected here. The DCR is an ADMIN upload
    //              today (uploaded = approved), so nothing new lands in this tab.
    //              Other operations (dispatch) never had a reject concept, so they
    //              return an empty set (the UI still shows the tab for consistency).
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
            // DCR ab ADMIN khud upload karta hai, isliye "abhi user ne upload
            // nahi kiya" wali chhaanti hata di gayi hai: DCRUpdate stage par
            // khadi HAR request yahan dikhni chahiye, warna admin ke paas use
            // kholne ka koi rasta hi nahi bachta.
            all = await _uow.SolarRequests.FindAsync(x => (int)x.CurrentStage >= (int)stage);
        }
        else if (mode == "approved")
        {
            // Past this stage = the operation completed for these requests.
            all = await _uow.SolarRequests.FindAsync(x => (int)x.CurrentStage > (int)stage);
        }
        else if (mode == "rejected")
        {
            // Legacy only: DCR used to be a user upload that the admin approved or
            // rejected. The admin uploads it now (no approve/reject step), so this
            // tab just keeps the old rejected rows visible. The dispatch modes
            // (meter/material/installation) never had a rejection state at all.
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
            // DCR pending queue = Installation ke baad DCRUpdate stage par khadi
            // har request. Pehle yahan sirf wo rows aati thin jinka DCR USER ne
            // upload kiya ho; ab upload admin ka kaam hai, to wo shart hata di gayi
            // hai - warna queue hamesha khaali rehti aur DCR kabhi hota hi nahi.
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

                // Material list: the master items the Prepare form asks a quantity
                // for, and what each dispatch already recorded (keyed by SolarRequestId).
                ViewBag.MaterialItemMaster = (await _uow.MaterialItems.FindAsync(i => i.IsActive))
                                             .OrderBy(i => i.SortOrder).ThenBy(i => i.Id)
                                             .ToList();
                var dispatchIds = materials.Values.Select(m => m.Id).ToHashSet();
                var lines = dispatchIds.Any()
                    ? (await _uow.MaterialDispatchItems.FindAsync(x => dispatchIds.Contains(x.MaterialDispatchId))).ToList()
                    : new List<MaterialDispatchItem>();
                ViewBag.MaterialDispatchItems = materials.ToDictionary(
                    kv => kv.Key,
                    kv => lines.Where(l => l.MaterialDispatchId == kv.Value.Id).OrderBy(l => l.Id).ToList());

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
        string? vehicleDetails, string? prepareRemark, int? workerId, IFormFile? dispatchDoc,
        string? materialItemsJson)
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

            // Material list quantities. Re-saving Prepare replaces the whole list,
            // so a cleared quantity drops that line.
            await SaveMaterialItemsAsync(dispatch.Id, materialItemsJson);

            // Log Report: which items (of the full list) were prepared, and how many.
            var prepLines = (await _uow.MaterialDispatchItems.FindAsync(x => x.MaterialDispatchId == dispatch.Id))
                            .OrderBy(x => x.Id).ToList();
            var totalItems = (await _uow.MaterialItems.FindAsync(i => i.IsActive)).Count();
            var installer = await _uow.Workers.GetByIdAsync(workerId.Value);
            await _activity.LogAsync(_userManager.GetUserId(User) ?? "system",
                "MaterialDispatch.Prepare", "SolarRequest", requestId.ToString(),
                $"Prepared for dispatch: {prepLines.Count} of {totalItems} items — " +
                (prepLines.Any()
                    ? string.Join(", ", prepLines.Select(l => string.IsNullOrWhiteSpace(l.Quantity) ? l.ItemName : $"{l.ItemName} × {l.Quantity}"))
                    : "no items ticked") +
                $". Installer: {installer?.Name ?? "#" + workerId}." +
                (string.IsNullOrWhiteSpace(vehicleDetails) ? "" : $" Vehicle: {vehicleDetails}.") +
                (string.IsNullOrWhiteSpace(materialDetails) ? "" : $" {materialDetails}"),
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

    private sealed record MaterialItemQty(int Id, string? Qty);

    /// <summary>
    /// Replaces the material lines of a dispatch with the items ticked on the
    /// Prepare form ([{ id, qty }]). Every posted item is stored — quantity may be
    /// blank; unknown or inactive item ids are ignored.
    /// </summary>
    private async Task SaveMaterialItemsAsync(int materialDispatchId, string? materialItemsJson)
    {
        var posted = new List<MaterialItemQty>();
        if (!string.IsNullOrWhiteSpace(materialItemsJson))
        {
            posted = System.Text.Json.JsonSerializer.Deserialize<List<MaterialItemQty>>(materialItemsJson,
                         new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                     ?? new List<MaterialItemQty>();
        }

        var master = (await _uow.MaterialItems.FindAsync(i => i.IsActive)).ToDictionary(i => i.Id);

        var existing = await _uow.MaterialDispatchItems.FindAsync(x => x.MaterialDispatchId == materialDispatchId);
        _uow.MaterialDispatchItems.RemoveRange(existing);

        foreach (var p in posted)
        {
            var qty = p.Qty?.Trim() ?? string.Empty;
            if (!master.TryGetValue(p.Id, out var item)) continue;
            await _uow.MaterialDispatchItems.AddAsync(new MaterialDispatchItem
            {
                MaterialDispatchId = materialDispatchId,
                MaterialItemId = item.Id,
                ItemName = item.Name,
                Quantity = qty.Length > 50 ? qty[..50] : qty
            });
        }
        await _uow.SaveChangesAsync();
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
        string? remark, IFormFile? dispatchDoc, string? materialItemsJson)
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

            // What actually goes out, item by item (e.g. prepared 10, sent 8). Checked
            // BEFORE anything is saved so a bad quantity never half-dispatches.
            List<(MaterialDispatchItem Line, decimal Qty)>? sendPlan = null;
            if (materialItemsJson != null)
            {
                var (planError, plan) = await PlanDispatchAsync(dispatch.Id, materialItemsJson, pendingOnly: false);
                if (planError != null) return Json(new { success = false, message = planError });
                sendPlan = plan;
            }

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

            // Record what was sent. The prepared quantity is kept; any shortfall
            // stays "pending" and can be sent later with Dispatch Pending.
            if (sendPlan != null)
            {
                foreach (var (line, qty) in sendPlan)
                {
                    line.DispatchedQuantity = qty;
                    _uow.MaterialDispatchItems.Update(line);
                }
                await _uow.SaveChangesAsync();
            }

            var stageResult = await _requestService.UpdateStageAsync(new UpdateSolarRequestStatusDto
            {
                Id = requestId,
                NewStage = ProjectStatus.Installation,
                Notes = $"Material dispatched on {dispatch.DispatchDate:dd/MM/yyyy}"
            }, _userManager.GetUserId(User)!);

            if (!stageResult.IsSuccess)
                return Json(new { success = false, message = $"Stage update failed: {stageResult.Message ?? string.Join("; ", stageResult.Errors)}" });

            // Log Report: exactly what went out, and what is still owed.
            var sentText = sendPlan == null || sendPlan.Count == 0
                ? "no item list"
                : string.Join(", ", sendPlan.Select(x =>
                    $"{x.Line.ItemName} × {FmtQty(x.Qty)}" +
                    (ParseQty(x.Line.Quantity) is decimal pq && pq != x.Qty ? $" (of {FmtQty(pq)})" : "")));
            var pendingText = sendPlan == null ? "" : string.Join(", ", sendPlan
                .Where(x => ParseQty(x.Line.Quantity) is decimal pq && pq > x.Qty)
                .Select(x => $"{x.Line.ItemName} × {FmtQty(ParseQty(x.Line.Quantity)!.Value - x.Qty)}"));
            await _activity.LogAsync(_userManager.GetUserId(User) ?? "system",
                "MaterialDispatch.Final", "SolarRequest", requestId.ToString(),
                $"Material dispatched on {dispatch.DispatchDate:dd/MM/yyyy}: {sentText}." +
                (pendingText.Length > 0 ? $" Pending: {pendingText}." : "") +
                " Project moved to Installation." +
                (string.IsNullOrWhiteSpace(remark) ? "" : $" Remark: {remark.Trim()}"),
                HttpContext.Connection.RemoteIpAddress?.ToString());

            return Json(new
            {
                success = true,
                message = "Material dispatched. Project moved to Installation." +
                          (pendingText.Length > 0 ? $" Pending: {pendingText} — send it later with Dispatch Pending." : "")
            });
        }
        catch (Exception ex)
        {
            var detail = ex.InnerException?.Message ?? ex.Message;
            return Json(new { success = false, message = $"Final dispatch failed: {detail}" });
        }
    }

    /// <summary>One material line on the dispatch View page.</summary>
    public sealed record DispatchLineView(string Name, string PreparedText, decimal? Prepared, decimal? Sent, decimal Pending);

    // GET: /SolarPanelAdmin/Operations/MaterialDispatchDetails/5 (id = SolarRequestId)
    // Read-only view of one project's material dispatch: what was prepared, what
    // went out, what is still pending, and the dated history from the activity log.
    public async Task<IActionResult> MaterialDispatchDetails(int id, string? from)
    {
        // Back / breadcrumb return to whichever list opened this page.
        ViewBag.FromAction = string.Equals(from, "prepare", StringComparison.OrdinalIgnoreCase)
            ? nameof(PrepareDispatch) : nameof(FinalDispatch);
        var req = await _uow.SolarRequests.GetByIdAsync(id);
        if (req == null) return NotFound();

        var dispatch = (await _uow.MaterialDispatches.FindAsync(m => m.SolarRequestId == id))
                       .OrderByDescending(m => m.CreatedAt).FirstOrDefault();

        var lines = new List<DispatchLineView>();
        if (dispatch != null)
        {
            lines = (await _uow.MaterialDispatchItems.FindAsync(x => x.MaterialDispatchId == dispatch.Id))
                .OrderBy(x => x.Id)
                .Select(l =>
                {
                    var prepared = ParseQty(l.Quantity);
                    // Once dispatched, anything not (fully) sent is owed — including
                    // items prepared later with "Prepare Remaining Items".
                    var pending = dispatch.IsDispatched && prepared.HasValue
                        ? Math.Max(0, prepared.Value - (l.DispatchedQuantity ?? 0)) : 0;
                    return new DispatchLineView(l.ItemName, l.Quantity, prepared, l.DispatchedQuantity, pending);
                })
                .ToList();
        }

        Worker? worker = dispatch?.AssignedWorkerId is int wid ? await _uow.Workers.GetByIdAsync(wid) : null;

        // Dispatch history (prepare / final / pending), oldest first, with admin names.
        var history = (await _activity.GetForEntityAsync("SolarRequest", id.ToString()))
                      .Where(a => a.Action.StartsWith("MaterialDispatch", StringComparison.OrdinalIgnoreCase))
                      .OrderBy(a => a.Timestamp).ToList();
        var names = new Dictionary<string, string>();
        foreach (var uid in history.Select(h => h.UserId).Where(u => !string.IsNullOrWhiteSpace(u)).Distinct())
        {
            var u = await _userManager.FindByIdAsync(uid);
            names[uid] = u == null ? uid : (!string.IsNullOrWhiteSpace(u.FullName) ? u.FullName! : u.UserName ?? uid);
        }

        ViewBag.Request = req;
        ViewBag.Dispatch = dispatch;
        ViewBag.Worker = worker;
        ViewBag.Lines = lines;
        ViewBag.TotalItems = (await _uow.MaterialItems.FindAsync(i => i.IsActive)).Count();
        ViewBag.History = history;
        ViewBag.AdminNames = names;
        ViewBag.Title = "Material Dispatch Details";
        return View();
    }

    /// <summary>Leading number of a quantity ("8", "8.5", "20 m" → 20); null when there is none.</summary>
    private static decimal? ParseQty(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var m = System.Text.RegularExpressions.Regex.Match(s, @"^\s*(\d+(?:\.\d+)?)");
        return m.Success && decimal.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Number,
                                             System.Globalization.CultureInfo.InvariantCulture, out var d)
            ? d : null;
    }

    private static string FmtQty(decimal d) => d.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Validates posted send quantities ([{ id, qty }], id = MaterialItemId) against
    /// a dispatch's prepared list and returns what to send per line.
    ///   Final (pendingOnly = false): every prepared line gets a sent quantity,
    ///     0 … prepared; a line not posted is treated as sent in full.
    ///   Pending (pendingOnly = true): 0 … what is still owed (prepared − sent);
    ///     only lines with a positive quantity are returned.
    /// </summary>
    private async Task<(string? Error, List<(MaterialDispatchItem Line, decimal Qty)> Plan)> PlanDispatchAsync(
        int materialDispatchId, string? json, bool pendingOnly)
    {
        var plan = new List<(MaterialDispatchItem, decimal)>();
        var posted = string.IsNullOrWhiteSpace(json)
            ? new List<MaterialItemQty>()
            : System.Text.Json.JsonSerializer.Deserialize<List<MaterialItemQty>>(json,
                  new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();

        var lines = (await _uow.MaterialDispatchItems.FindAsync(x => x.MaterialDispatchId == materialDispatchId)).ToList();

        foreach (var line in lines)
        {
            var p = posted.FirstOrDefault(x => x.Id == line.MaterialItemId);
            var prepared = ParseQty(line.Quantity);

            if (p == null)
            {
                if (!pendingOnly) plan.Add((line, prepared ?? 0));
                continue;
            }

            decimal qty = 0;
            if (!string.IsNullOrWhiteSpace(p.Qty) &&
                !(decimal.TryParse(p.Qty.Trim(), System.Globalization.NumberStyles.Number,
                                   System.Globalization.CultureInfo.InvariantCulture, out qty) && qty >= 0))
                return ($"{line.ItemName}: \"{p.Qty}\" is not a valid quantity.", plan);

            if (!pendingOnly)
            {
                if (prepared.HasValue && qty > prepared.Value)
                    return ($"{line.ItemName}: cannot send {FmtQty(qty)} — only {FmtQty(prepared.Value)} was prepared.", plan);
                plan.Add((line, qty));
            }
            else
            {
                if (qty == 0) continue;
                var owed = (prepared ?? 0) - (line.DispatchedQuantity ?? 0);
                if (qty > owed)
                    return ($"{line.ItemName}: only {FmtQty(Math.Max(0, owed))} is pending, cannot send {FmtQty(qty)}.", plan);
                plan.Add((line, (line.DispatchedQuantity ?? 0) + qty));
            }
        }
        return (null, plan);
    }

    // POST: after the first dispatch, prepare items from the list that were NOT
    // prepared the first time (e.g. 16 of 32 went, now the other 16). They are
    // added to the same dispatch as "prepared, not sent", so Final Dispatch offers
    // them under Dispatch Pending. Same installer, no stage change.
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SubmitPrepareMore(int requestId, string? materialItemsJson, string? prepareRemark)
    {
        try
        {
            var dispatch = (await _uow.MaterialDispatches.FindAsync(m => m.SolarRequestId == requestId))
                           .OrderByDescending(m => m.CreatedAt)
                           .FirstOrDefault();
            if (dispatch == null || !dispatch.IsDispatched)
                return Json(new { success = false, message = "Use Prepare for Dispatch — this project has not been dispatched yet." });

            var posted = string.IsNullOrWhiteSpace(materialItemsJson)
                ? new List<MaterialItemQty>()
                : System.Text.Json.JsonSerializer.Deserialize<List<MaterialItemQty>>(materialItemsJson,
                      new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();

            var master = (await _uow.MaterialItems.FindAsync(i => i.IsActive)).ToDictionary(i => i.Id);
            var existingIds = (await _uow.MaterialDispatchItems.FindAsync(x => x.MaterialDispatchId == dispatch.Id))
                              .Select(x => x.MaterialItemId).ToHashSet();

            var added = new List<MaterialDispatchItem>();
            foreach (var p in posted)
            {
                // Only items not already on this dispatch — existing lines are
                // handled by Dispatch Pending, never re-prepared here.
                if (existingIds.Contains(p.Id) || !master.TryGetValue(p.Id, out var item)) continue;
                // Quantity is required here: it is what Dispatch Pending will owe.
                var qty = p.Qty?.Trim() ?? string.Empty;
                if (!(decimal.TryParse(qty, System.Globalization.NumberStyles.Number,
                                       System.Globalization.CultureInfo.InvariantCulture, out var q) && q > 0))
                    return Json(new { success = false, message = $"{item.Name}: enter a quantity greater than 0." });

                var line = new MaterialDispatchItem
                {
                    MaterialDispatchId = dispatch.Id,
                    MaterialItemId = item.Id,
                    ItemName = item.Name,
                    Quantity = qty,
                    DispatchedQuantity = null      // prepared, not sent yet
                };
                await _uow.MaterialDispatchItems.AddAsync(line);
                added.Add(line);
                existingIds.Add(item.Id);
            }

            if (added.Count == 0)
                return Json(new { success = false, message = "Tick at least one item to prepare." });

            if (!string.IsNullOrWhiteSpace(prepareRemark))
            {
                var note = $"[Prepared more {DateTime.Today:dd/MM/yyyy}] {prepareRemark.Trim()}";
                dispatch.PrepareRemark = string.IsNullOrWhiteSpace(dispatch.PrepareRemark) ? note : $"{dispatch.PrepareRemark}\n{note}";
                _uow.MaterialDispatches.Update(dispatch);
            }
            await _uow.SaveChangesAsync();

            var list = string.Join(", ", added.Select(l => string.IsNullOrWhiteSpace(l.Quantity) ? l.ItemName : $"{l.ItemName} × {l.Quantity}"));
            await _activity.LogAsync(_userManager.GetUserId(User) ?? "system",
                "MaterialDispatch.PrepareMore", "SolarRequest", requestId.ToString(),
                $"Prepared {added.Count} more item(s): {list}. Now {existingIds.Count} of {master.Count} items prepared." +
                (string.IsNullOrWhiteSpace(prepareRemark) ? "" : $" Remark: {prepareRemark.Trim()}"),
                HttpContext.Connection.RemoteIpAddress?.ToString());

            return Json(new
            {
                success = true,
                message = $"{added.Count} more item(s) prepared. Send them from Final Dispatch → Dispatch Pending."
            });
        }
        catch (Exception ex)
        {
            var detail = ex.InnerException?.Message ?? ex.Message;
            return Json(new { success = false, message = $"Prepare failed: {detail}" });
        }
    }

    // POST: send items that were short at Final Dispatch. Same installer as the
    // first dispatch (it cannot be changed here) and NO stage change — the project
    // carries on with installation while the balance follows.
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SubmitPendingDispatch(int requestId, DateTime? dispatchDate,
        string? remark, string? materialItemsJson)
    {
        try
        {
            var dispatch = (await _uow.MaterialDispatches.FindAsync(m => m.SolarRequestId == requestId))
                           .OrderByDescending(m => m.CreatedAt)
                           .FirstOrDefault();
            if (dispatch == null || !dispatch.IsDispatched)
                return Json(new { success = false, message = "Do the Final Dispatch first — pending items can only follow it." });

            var (planError, plan) = await PlanDispatchAsync(dispatch.Id, materialItemsJson, pendingOnly: true);
            if (planError != null) return Json(new { success = false, message = planError });
            if (plan.Count == 0)
                return Json(new { success = false, message = "Enter a quantity for at least one pending item." });

            var sentNow = plan.Select(x => $"{x.Line.ItemName} × {FmtQty(x.Qty - (x.Line.DispatchedQuantity ?? 0))}").ToList();
            foreach (var (line, newTotal) in plan)
            {
                line.DispatchedQuantity = newTotal;
                _uow.MaterialDispatchItems.Update(line);
            }

            var when = dispatchDate ?? DateTime.Today;
            var note = $"[Pending dispatch {when:dd/MM/yyyy}] {string.Join(", ", sentNow)}" +
                       (string.IsNullOrWhiteSpace(remark) ? "" : $" — {remark.Trim()}");
            dispatch.Remark = string.IsNullOrWhiteSpace(dispatch.Remark) ? note : $"{dispatch.Remark}\n{note}";
            _uow.MaterialDispatches.Update(dispatch);
            await _uow.SaveChangesAsync();

            var stillOwed = (await _uow.MaterialDispatchItems.FindAsync(x => x.MaterialDispatchId == dispatch.Id))
                .Select(l => (l.ItemName, Owed: (ParseQty(l.Quantity) ?? 0) - (l.DispatchedQuantity ?? 0)))
                .Where(x => x.Owed > 0)
                .Select(x => $"{x.ItemName} × {FmtQty(x.Owed)}")
                .ToList();
            var owedText = stillOwed.Any() ? $" Still pending: {string.Join(", ", stillOwed)}." : " Nothing pending now.";

            await _activity.LogAsync(_userManager.GetUserId(User) ?? "system",
                "MaterialDispatch.Pending", "SolarRequest", requestId.ToString(),
                $"Pending material dispatched on {when:dd/MM/yyyy}: {string.Join(", ", sentNow)}.{owedText}" +
                (string.IsNullOrWhiteSpace(remark) ? "" : $" Remark: {remark.Trim()}"),
                HttpContext.Connection.RemoteIpAddress?.ToString());

            return Json(new { success = true, message = $"Pending material dispatched: {string.Join(", ", sentNow)}.{owedText}" });
        }
        catch (Exception ex)
        {
            var detail = ex.InnerException?.Message ?? ex.Message;
            return Json(new { success = false, message = $"Pending dispatch failed: {detail}" });
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

        // Newest submission first, like every other admin queue and report. (This
        // one used to sort oldest-first so the longest-waiting installer showed on
        // top; the admin asked for one consistent date-descending order instead.)
        var list = rows.OrderByDescending(i => i.SubmittedAt ?? i.CompletedAt ?? i.CreatedAt)
                       .ThenByDescending(i => i.Id)
                       .ToList();

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

    /// <summary>An installation's checklist state: its (non-deleted) photos and entries,
    /// whether it predates the checklist, and the per-item evaluation.</summary>
    public sealed class ChecklistState
    {
        public List<InstallationPhoto> Photos { get; init; } = new();
        public List<InstallationChecklistEntry> Entries { get; init; } = new();
        public bool IsLegacy { get; init; }
        public List<InstallationChecklist.LineStatus> Lines { get; init; } = new();
        public int Done => Lines.Count(l => l.IsComplete);
        public int Total => Lines.Count;
    }

    private async Task<ChecklistState> LoadChecklistAsync(int installationId)
    {
        // Soft-deleted (replaced) photos and entries are excluded by the query filters.
        var photos = (await _uow.InstallationPhotos.FindAsync(p => p.InstallationId == installationId))
                     .OrderBy(p => p.Id).ToList();
        var entries = (await _uow.InstallationChecklistEntries.FindAsync(e => e.InstallationId == installationId))
                      .OrderBy(e => e.Id).ToList();
        var legacy = InstallationChecklist.IsLegacy(photos, entries);
        var lines = legacy
            ? new List<InstallationChecklist.LineStatus>()
            : InstallationChecklist.Evaluate(await _uow.IncUploadFormats.GetAllAsync(), photos, entries);
        return new ChecklistState { Photos = photos, Entries = entries, IsLegacy = legacy, Lines = lines };
    }

    // GET: /SolarPanelAdmin/Operations/InstallationDetails/5 — everything the
    // installer submitted for one installation, item by item, with Approve / Reject.
    public async Task<IActionResult> InstallationDetails(int id)
    {
        var installation = await _uow.Installations.GetByIdAsync(id);
        if (installation == null) return NotFound();

        var req = await _uow.SolarRequests.GetByIdAsync(installation.SolarRequestId);
        var dispatch = (await _uow.MaterialDispatches.FindAsync(m => m.SolarRequestId == installation.SolarRequestId))
                       .OrderByDescending(m => m.CreatedAt).FirstOrDefault();

        var workerId = installation.AssignedWorkerId ?? dispatch?.AssignedWorkerId;
        var worker = workerId.HasValue ? await _uow.Workers.GetByIdAsync(workerId.Value) : null;

        // ReviewedBy holds a user id — show a readable name where possible.
        string? reviewedByName = installation.ReviewedBy;
        if (!string.IsNullOrWhiteSpace(installation.ReviewedBy))
        {
            var u = await _userManager.FindByIdAsync(installation.ReviewedBy);
            if (u != null) reviewedByName = !string.IsNullOrWhiteSpace(u.FullName) ? u.FullName : u.UserName;
        }

        ViewBag.Request = req;
        ViewBag.Dispatch = dispatch;
        ViewBag.Worker = worker;
        ViewBag.ReviewedByName = reviewedByName;
        ViewBag.Checklist = await LoadChecklistAsync(id);
        ViewBag.Title = "Installation Details";
        return View(installation);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ApproveInstallationPhotos(int installationId, string? remark)
    {
        try
        {
            var installation = await _uow.Installations.GetByIdAsync(installationId);
            if (installation == null)
                return Json(new { success = false, message = "Installation record not found." });

            var checklist = await LoadChecklistAsync(installationId);
            var photos = checklist.Photos;
            if (photos.Count == 0)
                return Json(new { success = false, message = "There are no photos to approve on this installation yet." });

            if (installation.ApprovalStatus == ApprovalStatus.Approved)
                return Json(new { success = false, message = "These photos are already approved." });

            // Checklist gate: every active checklist item must be complete.
            // Legacy installations (no checklist) keep the "at least one photo" rule.
            if (!checklist.IsLegacy)
            {
                var problems = InstallationChecklist.Problems(checklist.Lines);
                if (problems.Count > 0)
                    return Json(new
                    {
                        success = false,
                        problems,
                        message = "This installation cannot be approved yet — the checklist is incomplete:\n• " +
                                  string.Join("\n• ", problems)
                    });
            }

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
    //
    // DCR upload user panel se hata kar yahan laaya gaya hai: ab admin hi DCR
    // number, date, remark aur document bharta hai. Alag se approve karne ki
    // zaroorat nahi - admin ka upload hi approval hai (SubmitDCR seedha
    // Approved + project Completed karta hai).
    public async Task<IActionResult> DCRUpdate(string? state, string? city, string? filter)
    {
        var f = (filter ?? "all").ToLowerInvariant();
        var showHistory = f == "all";
        ViewBag.Filter = f;
        var requests = await FilterAsync(ProjectStatus.DCRUpdate, state, city, ConnectionType.Domestic, showHistory: showHistory, filterMode: f, op: "dcr");
        await PopulateFilterViewBags(state, city, requests);
        ViewBag.Title = "DCR & Work Upload";
        ViewBag.Op = "dcr";
        await PopulateOperationDetailsAsync("dcr", requests);
        return View("OperationsList", requests);
    }

    /// <summary>
    /// Admin uploads the DCR — the same four fields the user page used to ask for
    /// (number, date, document, remark).
    ///
    /// There is NO separate approval step: an admin upload is trusted, so the row is
    /// written straight as Approved / IsVerified and the project moves to Completed.
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SubmitDCR(int requestId, string dcrNumber,
        DateTime? dcrDate, string? remark, List<IFormFile>? dcrDocs)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dcrNumber))
                return Json(new { success = false, message = "DCR number is required" });

            // "DCR & Work Upload": exactly two files — the DCR and the work document.
            // PDF or image (Camera / Gallery). Zero files is allowed only when both
            // are already on record and the admin is just correcting number / date / remark.
            var allowedExt = new[] { ".pdf", ".jpg", ".jpeg", ".png" };
            var files = (dcrDocs ?? new List<IFormFile>()).Where(f => f != null && f.Length > 0).ToList();
            if (files.Any(f => !allowedExt.Contains(Path.GetExtension(f.FileName).ToLowerInvariant())))
                return Json(new { success = false, message = "Only PDF / JPG / PNG files are allowed" });

            // Upsert. A row already exists only for legacy projects where the USER
            // uploaded the DCR before that page moved to the admin panel - update
            // that same row rather than creating a duplicate.
            var dcr = (await _uow.DCRDocuments.FindAsync(d => d.SolarRequestId == requestId))
                      .OrderByDescending(d => d.Id)
                      .FirstOrDefault();
            bool isNew = dcr == null;

            var hasBoth = !string.IsNullOrWhiteSpace(dcr?.DocumentPath) &&
                          !string.IsNullOrWhiteSpace(dcr?.WorkDocumentPath);
            if (files.Count != 2 && !(files.Count == 0 && hasBoth))
                return Json(new { success = false, message = "Please upload exactly 2 files (DCR + Work document)" });

            var paths = new List<string>();
            foreach (var file in files)
            {
                var (ok, path, err) = await _fileUploadService.UploadAsync(file, "dcr");
                if (!ok || string.IsNullOrWhiteSpace(path))
                    return Json(new { success = false, message = $"Document upload failed: {err}" });
                paths.Add(path);
            }

            if (isNew) dcr = new DCRDocument { SolarRequestId = requestId };

            dcr!.DCRNumber = dcrNumber;
            dcr.DCRDate = dcrDate ?? dcr.DCRDate ?? DateTime.UtcNow;
            if (paths.Count == 2)                                     // re-upload replaces both files
            {
                dcr.DocumentPath = paths[0];
                dcr.WorkDocumentPath = paths[1];
            }
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
