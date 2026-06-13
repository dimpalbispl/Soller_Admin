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

/// <summary>
/// Admin Payment Verification — approve/reject user payments.
/// When cumulative *verified* amount reaches ₹20,000, the project's stage
/// is auto-advanced from Payment → PMSurvey here (NOT on the user side).
/// </summary>
[Area("SolarPanelAdmin")]
[Authorize(Roles = "Admin,SuperAdmin")]
public class PaymentsController : Controller
{
    private readonly IUnitOfWork _uow;
    private readonly IPaymentService _payments;
    private readonly ISolarRequestService _requestService;
    private readonly INotificationService _notifications;
    private readonly IFileUploadService _fileUploadService;
    private readonly ILegacyMlmApprovalService _legacyMlm;
    private readonly UserManager<ApplicationUser> _userManager;

    public PaymentsController(
        IUnitOfWork uow,
        IPaymentService payments,
        ISolarRequestService requestService,
        INotificationService notifications,
        IFileUploadService fileUploadService,
        ILegacyMlmApprovalService legacyMlm,
        UserManager<ApplicationUser> userManager)
    {
        _uow = uow;
        _payments = payments;
        _requestService = requestService;
        _notifications = notifications;
        _fileUploadService = fileUploadService;
        _legacyMlm = legacyMlm;
        _userManager = userManager;
    }

    // GET: /Admin/Payments
    public async Task<IActionResult> Index(string? filter)
    {
        // ===== Auto-heal: stage rollback + advance =====
        // Per spec: PMSurvey stage tab tak nahi aana chahiye jab tak full payment.
        // Two heals run on every admin Payments page load:
        //   1. ADVANCE: requests stuck at Payment/Registration with full verified
        //      payment → move to PMSurvey
        //   2. ROLLBACK: requests sitting at PMSurvey/MeterDispatch/etc. but whose
        //      verified payment is now LESS than PlanAmount (because a payment was
        //      rejected, or the project amount was increased) → move back to Payment
        try
        {
            var allRequests = await _uow.SolarRequests.GetAllAsync();
            var adminId = _userManager.GetUserId(User) ?? "system";

            // (1) ADVANCE — Payment → PMSurvey when fully paid
            var stuck = allRequests.Where(r =>
                r.CurrentStage == ProjectStatus.Registration ||
                r.CurrentStage == ProjectStatus.ProductSelection ||
                r.CurrentStage == ProjectStatus.Payment).ToList();
            foreach (var r in stuck)
            {
                var verified = await _payments.GetVerifiedPaidAsync(r.Id);
                if (r.PlanAmount > 0 && verified >= r.PlanAmount)
                {
                    await _requestService.UpdateStageAsync(new UpdateSolarRequestStatusDto
                    {
                        Id = r.Id,
                        NewStage = ProjectStatus.PMSurvey,
                        Notes = $"Auto-advanced on admin payments load. Verified ₹{verified:N0} ≥ project total ₹{r.PlanAmount:N0}."
                    }, adminId);
                }
            }

            // (2) ROLLBACK — PMSurvey → Payment when NOT fully paid.
            // Only touch PMSurvey itself (don't undo MeterDispatch+ which mean
            // admin/operations already did downstream work). This fixes the case
            // shown in the screenshot: Stage was PMSurvey but Paid=₹20K / Plan=₹30K.
            var advanced = allRequests.Where(r => r.CurrentStage == ProjectStatus.PMSurvey).ToList();
            foreach (var r in advanced)
            {
                var verified = await _payments.GetVerifiedPaidAsync(r.Id);
                if (r.PlanAmount > 0 && verified < r.PlanAmount)
                {
                    await _requestService.UpdateStageAsync(new UpdateSolarRequestStatusDto
                    {
                        Id = r.Id,
                        NewStage = ProjectStatus.Payment,
                        Notes = $"Auto-rolled back to Payment. Verified ₹{verified:N0} < project total ₹{r.PlanAmount:N0}."
                    }, adminId);
                }
            }
        }
        catch { /* non-fatal; continue showing the page */ }

        var all = await _uow.Payments.GetAllAsync();
        var rows = filter switch
        {
            "pending"  => all.Where(p => !p.IsVerified && p.Status != PaymentStatus.Rejected),
            "verified" => all.Where(p => p.IsVerified),
            "rejected" => all.Where(p => p.Status == PaymentStatus.Rejected),
            _          => all
        };

        // ===== Deduplicate =====
        // Per spec: "Payment Verification me duplicate records remove karo."
        // A user occasionally submits the same proof twice (re-uploads receipt and re-submits).
        // We group by (SolarRequestId + normalized UTR) and keep the most-progressed record:
        //   verified > pending > rejected, with newest CreatedAt as tiebreaker.
        // This way a verified row always wins over its duplicate pending row.
        static int Rank(Payment p) =>
            p.IsVerified                          ? 3 :
            p.Status == PaymentStatus.Rejected    ? 1 :
                                                    2;   // pending

        rows = rows
            .GroupBy(p => new {
                p.SolarRequestId,
                Utr = (p.UTRNumber ?? "").Trim().ToUpperInvariant()
            })
            .Select(g => g.OrderByDescending(Rank)
                          .ThenByDescending(p => p.CreatedAt)
                          .First())
            .OrderByDescending(p => p.CreatedAt)
            .ToList();

        // hydrate request numbers
        var reqIds = rows.Select(r => r.SolarRequestId).Distinct().ToList();
        var requests = (await _uow.SolarRequests.GetAllAsync())
                        .Where(r => reqIds.Contains(r.Id))
                        .ToDictionary(r => r.Id);

        // Per-request paid totals so the row can show Request / Paid / Due alongside this entry.
        // Sequential awaits — EF Core forbids concurrent ops on the same DbContext.
        var paidMap = new Dictionary<int, decimal>();
        foreach (var rid in reqIds)
        {
            paidMap[rid] = await _payments.GetTotalPaidAsync(rid);
        }
        ViewBag.PaidMap = paidMap;
        ViewBag.Requests = requests;
        ViewBag.Filter = filter ?? "all";
        return View(rows);
    }

    // POST: /Admin/Payments/Verify/{id}
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Verify(int id)
    {
        try
        {
            var payment = await _uow.Payments.GetByIdAsync(id);
            if (payment == null)
                return Json(new { success = false, message = "Payment not found" });

            // === Idempotency guards ===
            // Once a payment is verified, repeated Verify calls return a clear error
            // instead of silently re-running side effects (notifications, stage gate, etc.)
            if (payment.IsVerified)
                return Json(new { success = false, message = "Payment is already verified. Duplicate approval not allowed." });

            // A rejected payment must NOT be verifiable from the same record —
            // user has to submit a new payment proof. This prevents the
            // reject → re-approve loop that allowed state to flip freely.
            if (payment.Status == PaymentStatus.Rejected)
                return Json(new { success = false, message = "This payment was rejected and cannot be verified. Ask the user to submit a new payment." });

            var adminId = _userManager.GetUserId(User)!;
            var result = await _payments.VerifyAsync(id, adminId);
            if (!result.IsSuccess)
                return Json(new { success = false, message = result.Message });

            // Notify user that this payment was verified
            await _notifications.CreateAsync(new CreateNotificationDto
            {
                UserId = payment.UserId,
                SolarRequestId = payment.SolarRequestId,
                Title = "Payment verified",
                Message = $"Your payment of ₹{payment.Amount:N0} (UTR {payment.UTRNumber}) has been verified by admin.",
                NotificationType = "Payment"
            });

            // ====== Stage gate: advance to PM Surya Ghar ONLY when full project payment is verified ======
            // Per spec: "Jab tak full project amount complete nahi hota, PM Surya Ghar
            // step locked rahe." The ₹20K minimum still triggers Mode-2 auto-activation
            // (legacy rule), but the stage advance now requires verifiedTotal >= PlanAmount.
            var verifiedTotal = await _payments.GetVerifiedPaidAsync(payment.SolarRequestId);
            var min           = PaymentService.MinimumPaymentThreshold;
            var stageAdvanced = false;
            var autoActivated = false;

            if (verifiedTotal >= min)
            {
                var req = await _uow.SolarRequests.GetByIdAsync(payment.SolarRequestId);

                // === Mode 2 auto-activation rule (per spec) ===
                // If the request was created under "Only Solar without Activation" mode,
                // the user's account stays inactive until payment is verified. Once admin
                // approves payment, automatically activate the user.
                // Note: this still uses the ₹20K minimum (account activation, not stage advance).
                if (req != null && req.RequestType == RequestType.OnlySolarWithoutActivation)
                {
                    var owner = await _userManager.FindByIdAsync(payment.UserId);
                    if (owner != null && !owner.IsActive)
                    {
                        owner.IsActive = true;
                        owner.EmailConfirmed = true;
                        await _userManager.UpdateAsync(owner);
                        autoActivated = true;

                        await _notifications.CreateAsync(new CreateNotificationDto
                        {
                            UserId = owner.Id,
                            Title = "Account activated",
                            Message = "Your account has been auto-activated after payment verification. You can now sign in.",
                            NotificationType = "Account"
                        });
                    }
                }

                // FULL-PAYMENT gate for stage advance. Project amount must be fully paid.
                if (req != null &&
                    req.PlanAmount > 0 &&
                    verifiedTotal >= req.PlanAmount &&
                    (req.CurrentStage == ProjectStatus.Registration ||
                     req.CurrentStage == ProjectStatus.ProductSelection ||
                     req.CurrentStage == ProjectStatus.Payment))
                {
                    var stageResult = await _requestService.UpdateStageAsync(new UpdateSolarRequestStatusDto
                    {
                        Id = payment.SolarRequestId,
                        NewStage = ProjectStatus.PMSurvey,
                        Notes = $"Verified payments total ₹{verifiedTotal:N0} ≥ project total ₹{req.PlanAmount:N0} — advanced to PM Surya Ghar by admin."
                    }, adminId);

                    if (stageResult.IsSuccess)
                    {
                        stageAdvanced = true;
                        await _notifications.CreateAsync(new CreateNotificationDto
                        {
                            UserId = payment.UserId,
                            SolarRequestId = payment.SolarRequestId,
                            Title = "Workflow advanced",
                            Message = "Full project payment verified. You can now upload PM Surya Ghar documents.",
                            NotificationType = "StatusUpdate"
                        });
                    }
                }

                // ====== Legacy MLM approval chain (per spec) ======
                // Per Hinglish spec: "package name With Activation usk liye hi
                // ye approve reject pr lgana h other name jese without activation
                // aready active aata h to ye call nhi krna h"
                //
                // For WithActivation requests, port the VB AprvAction(Y) chain:
                //   Repurchincome insert + Sp_ActivateMember_New + TrnOrder +
                //   TrnorderDetail + TrnPaymentConfirmation + UserHistory +
                //   UPDATE TrnProductorderDetail SET IsApprove='Y'
                //
                // For OnlySolarWithoutActivation / AlreadyActiveOnlyRequest the
                // legacy MLM workflow doesn't apply — skip silently.
                //
                // Failures here are NON-FATAL: payment stays verified, stage
                // stays advanced. Admin gets a warning so they can reconcile
                // in the legacy admin manually if needed.
                if (req != null && req.RequestType == RequestType.WithActivation)
                {
                    var mlmRes = await _legacyMlm.ApproveActivationAsync(new LegacyMlmApprovalInput
                    {
                        MemberIdNo    = payment.UserId,
                        Utr           = payment.UTRNumber,
                        Amount        = payment.Amount,
                        ApproveRemark = "Admin verified payment",
                        PartyCode     = adminId,
                        RequestNumber = req.RequestNumber
                    });
                    if (!mlmRes.Success)
                    {
                        TempData["Warning"] = "Payment verified, but legacy MLM approval failed: " +
                                              (mlmRes.ErrorMessage ?? "unknown error") +
                                              ". Reconcile via the legacy admin or retry.";
                    }
                    // If AlreadyProcessed == true: idempotent skip, nothing to do.
                }
            }

            var msg = stageAdvanced
                ? $"Payment verified. Verified total ₹{verifiedTotal:N0} meets ₹{min:N0} minimum — project moved to PM Surya Ghar."
                : verifiedTotal < min
                    ? $"Payment verified. Verified total ₹{verifiedTotal:N0} of ₹{min:N0} minimum — needs ₹{min - verifiedTotal:N0} more to advance."
                    : "Payment verified.";
            if (autoActivated)
                msg += " User account auto-activated (Only Solar mode).";

            return Json(new
            {
                success = true,
                verifiedTotal = verifiedTotal,
                minimum = min,
                stageAdvanced = stageAdvanced,
                autoActivated = autoActivated,
                message = msg
            });
        }
        catch (Exception ex)
        {
            var detail = ex.InnerException?.Message ?? ex.Message;
            return Json(new { success = false, message = $"Verify failed: {detail}" });
        }
    }

    // POST: /Admin/Payments/Reject/{id}
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Reject(int id, string? reason)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(reason))
                return Json(new { success = false, message = "Rejection reason is required." });

            var payment = await _uow.Payments.GetByIdAsync(id);
            if (payment == null)
                return Json(new { success = false, message = "Payment not found" });

            // === Idempotency guards ===
            // Previously, Reject set Status back to Pending and IsVerified=false,
            // which meant the same row could ping-pong between approved/rejected.
            // Now reject is a TERMINAL state for that payment row — the user must
            // submit a fresh proof. This is what closes the SE86372259-style bug.
            if (payment.Status == PaymentStatus.Rejected)
                return Json(new { success = false, message = "Payment is already rejected. Duplicate rejection not allowed." });

            if (payment.IsVerified)
                return Json(new { success = false, message = "Cannot reject an already-verified payment." });

            payment.Status = PaymentStatus.Rejected;
            payment.IsVerified = false;
            payment.VerifiedBy = _userManager.GetUserId(User);
            payment.VerifiedAt = DateTime.UtcNow;   // re-using as "decision timestamp"
            payment.Notes = (payment.Notes ?? "") + $"\n[REJECTED by admin] {reason}";
            _uow.Payments.Update(payment);
            await _uow.SaveChangesAsync();

            await _notifications.CreateAsync(new CreateNotificationDto
            {
                UserId = payment.UserId,
                SolarRequestId = payment.SolarRequestId,
                Title = "Payment rejected",
                Message = $"Your payment of ₹{payment.Amount:N0} was rejected. Reason: {reason}. Please submit a new payment proof.",
                NotificationType = "Payment"
            });

            // ====== Legacy MLM reject (per spec — WithActivation only) ======
            // Mirror of the approve gate above: legacy TrnProductorderDetail row
            // gets marked IsApprove='R' so the legacy admin's Pending queue
            // matches our new admin's state. Non-WithActivation modes never
            // had a legacy row to begin with, so we skip them.
            //
            // Failures are NON-FATAL: payment is already rejected in our DB;
            // admin gets a warning to manually reconcile the legacy row.
            var rejReq = await _uow.SolarRequests.GetByIdAsync(payment.SolarRequestId);
            if (rejReq != null && rejReq.RequestType == RequestType.WithActivation)
            {
                var mlmRes = await _legacyMlm.RejectActivationAsync(new LegacyMlmRejectInput
                {
                    MemberIdNo    = payment.UserId,
                    Utr           = payment.UTRNumber,
                    RejectRemark  = reason!,
                    RequestNumber = rejReq.RequestNumber
                });
                if (!mlmRes.Success)
                {
                    TempData["Warning"] = "Payment rejected, but legacy MLM reject failed: " +
                                          (mlmRes.ErrorMessage ?? "unknown error") +
                                          ". Reconcile via the legacy admin manually.";
                }
            }

            return Json(new { success = true, message = "Payment rejected. User notified." });
        }
        catch (Exception ex)
        {
            var detail = ex.InnerException?.Message ?? ex.Message;
            return Json(new { success = false, message = $"Reject failed: {detail}" });
        }
    }

    // POST: /Admin/Payments/AddByAdmin
    // Admin-side payment entry. Bypasses the ₹20K minimum and the project-total cap rules
    // that apply to user-submitted payments. Admin can record any amount on behalf of user.
    // Payment is auto-marked as Verified (since admin is recording it directly).
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddByAdmin(int solarRequestId, decimal amount, string utrNumber,
        DateTime? paymentDate, string? referenceNumber, string? notes, IFormFile? receiptImage)
    {
        try
        {
            if (amount <= 0)
                return Json(new { success = false, message = "Amount must be greater than zero." });
            if (string.IsNullOrWhiteSpace(utrNumber))
                return Json(new { success = false, message = "UTR number is required." });

            var req = await _uow.SolarRequests.GetByIdAsync(solarRequestId);
            if (req == null)
                return Json(new { success = false, message = "Solar request not found." });

            // ===== Double-submit / multi-click guard =====
            // Symptom: "admin se payment kiya h jo multiclick ho rha h to double
            // double entry ja rhi h". Modal's submit handler called fetch() but
            // didn't disable the button, so a double-click produced two POSTs and
            // two ledger entries. We defend on the server too — if the SAME
            // (request, UTR) was already recorded, reject the second one.
            var utrNormalized = utrNumber.Trim();
            var existingDupe = (await _uow.Payments.GetAllAsync())
                .Any(p =>
                    p.SolarRequestId == solarRequestId &&
                    !string.IsNullOrEmpty(p.UTRNumber) &&
                    p.UTRNumber.Trim().Equals(utrNormalized, StringComparison.OrdinalIgnoreCase));
            if (existingDupe)
                return Json(new
                {
                    success = false,
                    message = $"A payment for this request with UTR '{utrNormalized}' already exists. " +
                              "Duplicate entry blocked. Refresh the page to see the existing record."
                });

            string? receiptPath = null;
            if (receiptImage != null)
            {
                var (ok, path, err) = await _fileUploadService.UploadAsync(receiptImage, "payments");
                if (!ok)
                    return Json(new { success = false, message = $"Receipt upload failed: {err}" });
                receiptPath = path;
            }

            var adminId   = _userManager.GetUserId(User) ?? "system";
            var adminUser = await _userManager.GetUserAsync(User);
            var adminName = adminUser?.FullName ?? adminUser?.UserName ?? "Admin";

            var payment = new Payment
            {
                SolarRequestId   = solarRequestId,
                UserId           = req.UserId,                 // attribute to project owner
                Amount           = amount,
                UTRNumber        = utrNormalized,
                ReferenceNumber  = referenceNumber?.Trim(),
                PaymentDate      = paymentDate ?? DateTime.UtcNow,
                PaymentMethod    = "Admin Entry",
                ReceiptImagePath = receiptPath,
                Status           = PaymentStatus.Completed,
                IsVerified       = true,                       // admin-entered = pre-verified
                VerifiedBy       = adminId,
                VerifiedAt       = DateTime.UtcNow,
                Notes            = $"[ADMIN ENTRY by {adminName}] {(notes ?? "")}".Trim()
            };

            await _uow.Payments.AddAsync(payment);
            await _uow.SaveChangesAsync();

            // Notify project owner so they see the admin-added payment immediately
            await _notifications.CreateAsync(new CreateNotificationDto
            {
                UserId         = req.UserId,
                SolarRequestId = solarRequestId,
                Title          = "Payment recorded by admin",
                Message        = $"Admin {adminName} recorded a payment of ₹{amount:N0} (UTR {utrNumber}) on your behalf.",
                NotificationType = "Payment"
            });

            // Stage gate: same rule as user-verify path — full PlanAmount required to advance to PMSurvey.
            var verifiedTotal = await _payments.GetVerifiedPaidAsync(solarRequestId);
            var stageAdvanced = false;

            if (req.PlanAmount > 0 &&
                verifiedTotal >= req.PlanAmount &&
                (req.CurrentStage == ProjectStatus.Registration ||
                 req.CurrentStage == ProjectStatus.ProductSelection ||
                 req.CurrentStage == ProjectStatus.Payment))
            {
                var stageResult = await _requestService.UpdateStageAsync(new UpdateSolarRequestStatusDto
                {
                    Id       = solarRequestId,
                    NewStage = ProjectStatus.PMSurvey,
                    Notes    = $"Admin-recorded payment brought verified total to ₹{verifiedTotal:N0} ≥ project total ₹{req.PlanAmount:N0}."
                }, adminId);
                stageAdvanced = stageResult.IsSuccess;
            }

            return Json(new
            {
                success       = true,
                paymentId     = payment.Id,
                verifiedTotal = verifiedTotal,
                stageAdvanced = stageAdvanced,
                message       = stageAdvanced
                    ? $"Payment of ₹{amount:N0} recorded. Verified total ₹{verifiedTotal:N0} — project advanced to PM Surya Ghar."
                    : $"Payment of ₹{amount:N0} recorded. Verified total now ₹{verifiedTotal:N0}."
            });
        }
        catch (Exception ex)
        {
            var detail = ex.InnerException?.Message ?? ex.Message;
            return Json(new { success = false, message = $"Add payment failed: {detail}" });
        }
    }
}
