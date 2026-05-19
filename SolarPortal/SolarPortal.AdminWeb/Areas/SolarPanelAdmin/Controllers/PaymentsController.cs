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
    private readonly UserManager<ApplicationUser> _userManager;

    public PaymentsController(
        IUnitOfWork uow,
        IPaymentService payments,
        ISolarRequestService requestService,
        INotificationService notifications,
        IFileUploadService fileUploadService,
        UserManager<ApplicationUser> userManager)
    {
        _uow = uow;
        _payments = payments;
        _requestService = requestService;
        _notifications = notifications;
        _fileUploadService = fileUploadService;
        _userManager = userManager;
    }

    // GET: /Admin/Payments
    public async Task<IActionResult> Index(string? filter)
    {
        // ===== Auto-heal: advance any request whose verified payments already meet ₹20K =====
        // This catches edge cases where a prior Verify call's stage advance failed silently.
        try
        {
            var allRequests = await _uow.SolarRequests.GetAllAsync();
            var stuck = allRequests.Where(r =>
                r.CurrentStage == ProjectStatus.Registration ||
                r.CurrentStage == ProjectStatus.ProductSelection ||
                r.CurrentStage == ProjectStatus.Payment).ToList();
            var adminId = _userManager.GetUserId(User) ?? "system";
            foreach (var r in stuck)
            {
                var verified = await _payments.GetVerifiedPaidAsync(r.Id);
                if (verified >= PaymentService.MinimumPaymentThreshold)
                {
                    await _requestService.UpdateStageAsync(new UpdateSolarRequestStatusDto
                    {
                        Id = r.Id,
                        NewStage = ProjectStatus.PMSurvey,
                        Notes = $"Auto-advanced on admin payments load. Verified ₹{verified:N0} ≥ ₹{PaymentService.MinimumPaymentThreshold:N0}."
                    }, adminId);
                }
            }
        }
        catch { /* non-fatal; continue showing the page */ }

        var all = await _uow.Payments.GetAllAsync();
        var rows = filter switch
        {
            "pending"  => all.Where(p => !p.IsVerified),
            "verified" => all.Where(p => p.IsVerified),
            _          => all
        };
        rows = rows.OrderByDescending(p => p.CreatedAt).ToList();

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

            if (payment.IsVerified)
                return Json(new { success = false, message = "Payment is already verified." });

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

            // ====== Stage gate: advance to PM Surya Ghar when VERIFIED total ≥ ₹20,000 ======
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

                if (req != null &&
                    (req.CurrentStage == ProjectStatus.Registration ||
                     req.CurrentStage == ProjectStatus.ProductSelection ||
                     req.CurrentStage == ProjectStatus.Payment))
                {
                    var stageResult = await _requestService.UpdateStageAsync(new UpdateSolarRequestStatusDto
                    {
                        Id = payment.SolarRequestId,
                        NewStage = ProjectStatus.PMSurvey,
                        Notes = $"Verified payments total ₹{verifiedTotal:N0} ≥ minimum ₹{min:N0} — advanced to PM Surya Ghar by admin."
                    }, adminId);

                    if (stageResult.IsSuccess)
                    {
                        stageAdvanced = true;
                        await _notifications.CreateAsync(new CreateNotificationDto
                        {
                            UserId = payment.UserId,
                            SolarRequestId = payment.SolarRequestId,
                            Title = "Workflow advanced",
                            Message = "Minimum verified payment reached. You can now upload PM Surya Ghar documents.",
                            NotificationType = "StatusUpdate"
                        });
                    }
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
            var payment = await _uow.Payments.GetByIdAsync(id);
            if (payment == null)
                return Json(new { success = false, message = "Payment not found" });

            payment.Status = PaymentStatus.Pending;
            payment.IsVerified = false;
            payment.Notes = (payment.Notes ?? "") + $"\n[REJECTED by admin] {reason}";
            _uow.Payments.Update(payment);
            await _uow.SaveChangesAsync();

            await _notifications.CreateAsync(new CreateNotificationDto
            {
                UserId = payment.UserId,
                SolarRequestId = payment.SolarRequestId,
                Title = "Payment rejected",
                Message = $"Your payment of ₹{payment.Amount:N0} was rejected. Reason: {reason}",
                NotificationType = "Payment"
            });

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
                UTRNumber        = utrNumber.Trim(),
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

            // Stage gate: same rule as user-verify path — advance to PMSurvey when verified ≥ ₹20K
            var verifiedTotal = await _payments.GetVerifiedPaidAsync(solarRequestId);
            var min = PaymentService.MinimumPaymentThreshold;
            var stageAdvanced = false;

            if (verifiedTotal >= min &&
                (req.CurrentStage == ProjectStatus.Registration ||
                 req.CurrentStage == ProjectStatus.ProductSelection ||
                 req.CurrentStage == ProjectStatus.Payment))
            {
                var stageResult = await _requestService.UpdateStageAsync(new UpdateSolarRequestStatusDto
                {
                    Id       = solarRequestId,
                    NewStage = ProjectStatus.PMSurvey,
                    Notes    = $"Admin-recorded payment brought verified total to ₹{verifiedTotal:N0} ≥ ₹{min:N0}."
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
