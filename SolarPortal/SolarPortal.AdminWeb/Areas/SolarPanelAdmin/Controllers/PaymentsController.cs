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
    private readonly IAdminFundService _funds;
    private readonly IActiveIdDepositService _deposits;
    private readonly UserManager<ApplicationUser> _userManager;

    public PaymentsController(
        IUnitOfWork uow,
        IPaymentService payments,
        ISolarRequestService requestService,
        INotificationService notifications,
        IFileUploadService fileUploadService,
        IAdminFundService funds,
        IActiveIdDepositService deposits,
        UserManager<ApplicationUser> userManager)
    {
        _uow = uow;
        _payments = payments;
        _requestService = requestService;
        _notifications = notifications;
        _fileUploadService = fileUploadService;
        _funds = funds;
        _deposits = deposits;
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
                // Point 1: the Already-Active deposit is money on this project, so
                // it counts towards the full-payment gate. Without it a member who
                // has genuinely paid in full stays stuck at the Payment stage.
                var verified = await _payments.GetVerifiedPaidAsync(r.Id)
                             + await _deposits.GetForMemberAsync(
                                   r.RequestType == RequestType.AlreadyActiveOnlyRequest ? r.UserId : string.Empty);
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

            // (2) No rollback. The admin workflow should not automatically move
            // requests backwards from PMSurvey when a payment record later changes.
            // This preserves the ability to complete PM Surya / dispatch tasks
            // even when the project has not yet reached full verified payment.
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
        // Only VERIFIED (approved) payments count toward "Paid" — rejected and still-pending
        // payments must NOT inflate the paid total (they were never confirmed money). This
        // keeps the column consistent with the "Verified total ₹X of ₹20,000" verify message.
        // Sequential awaits — EF Core forbids concurrent ops on the same DbContext.
        var paidMap = new Dictionary<int, decimal>();
        foreach (var rid in reqIds)
        {
            paidMap[rid] = await _payments.GetVerifiedPaidAsync(rid);
        }

        // Point 1: an Already-Active member has already paid for the cPanel order
        // that activated their ID, and that money sits against this project. It is
        // not a Payment row, so without this the page bills them for it a second
        // time - SCR-007 showed "₹19,900 due" on a project the member had in fact
        // paid in full.
        //
        // Folded into paidMap rather than handled separately so every figure on the
        // page - row due, summary due, the Add Payment dropdown - is corrected in
        // one place and none of them can drift apart. DepositMap is passed too, so
        // the row can say where the extra money came from instead of looking like
        // an arithmetic bug.
        var depositMap = await _deposits.GetForRequestsAsync(requests.Values);
        foreach (var kv in depositMap)
        {
            if (paidMap.ContainsKey(kv.Key)) paidMap[kv.Key] += kv.Value;
        }

        ViewBag.DepositMap = depositMap;
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

            // ====== Approval gate ======
            // Verifying a payment IS the approval in this panel - the separate
            // Approvals module was removed, leaving Payment Verification as the one
            // entry-point. Point 4 then removed the full-payment condition: once the
            // ₹20,000 minimum is met the request is approved and PM Surya Ghar opens,
            // and the balance can follow.
            var verifiedTotal = await _payments.GetVerifiedPaidAsync(payment.SolarRequestId);

            // Same rule as the page-load heal: an Already-Active member's cPanel
            // deposit is part of what this project has received.
            var reqForDeposit = await _uow.SolarRequests.GetByIdAsync(payment.SolarRequestId);
            if (reqForDeposit?.RequestType == RequestType.AlreadyActiveOnlyRequest)
                verifiedTotal += await _deposits.GetForMemberAsync(reqForDeposit.UserId);
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

                // ── Point 4 ──────────────────────────────────────────────
                // "Kisi bhi ID ka Solar Request agar approve ho jaati hai to PM
                //  Surya then open ho jayega - poora payment ki zaroorat nahi hai."
                //
                // Payment Verification is the single approval entry-point in this
                // panel (the separate Approvals module was removed), so verifying a
                // payment IS the approval. It used to demand the whole PlanAmount
                // before advancing, which is exactly what point 4 removes: an admin
                // who verified 20,000 of a 30,000 project saw nothing happen and the
                // member stayed stuck on "unlocks after your request is approved".
                //
                // The 20,000 minimum still applies - it is the `verifiedTotal >= min`
                // block this sits inside - and the deposit counts towards it.
                if (req != null &&
                    (req.ApprovalStatus != ApprovalStatus.Approved ||
                     req.CurrentStage == ProjectStatus.Registration ||
                     req.CurrentStage == ProjectStatus.ProductSelection ||
                     req.CurrentStage == ProjectStatus.Payment))
                {
                    // Approve the request itself. Rejected requests are left alone -
                    // re-opening one silently on a payment would undo a deliberate
                    // admin decision.
                    if (req.ApprovalStatus == ApprovalStatus.Pending)
                    {
                        req.ApprovalStatus = ApprovalStatus.Approved;
                        req.UpdatedAt = DateTime.UtcNow;
                        _uow.SolarRequests.Update(req);
                        await _uow.SaveChangesAsync();
                    }

                    if (req.ApprovalStatus == ApprovalStatus.Approved &&
                        (req.CurrentStage == ProjectStatus.Registration ||
                         req.CurrentStage == ProjectStatus.ProductSelection ||
                         req.CurrentStage == ProjectStatus.Payment))
                    {
                        var fullyPaid = req.PlanAmount > 0 && verifiedTotal >= req.PlanAmount;
                        var stageResult = await _requestService.UpdateStageAsync(new UpdateSolarRequestStatusDto
                        {
                            Id = payment.SolarRequestId,
                            NewStage = ProjectStatus.PMSurvey,
                            Notes = fullyPaid
                                ? $"Verified payments total {verifiedTotal:N0} >= project total {req.PlanAmount:N0} - advanced to PM Surya Ghar by admin."
                                : $"Verified payments total {verifiedTotal:N0} meets the {min:N0} minimum - request approved and PM Surya Ghar opened. Balance can follow."
                        }, adminId);

                        if (stageResult.IsSuccess)
                        {
                            stageAdvanced = true;
                            await _notifications.CreateAsync(new CreateNotificationDto
                            {
                                UserId = payment.UserId,
                                SolarRequestId = payment.SolarRequestId,
                                Title = "Request approved",
                                Message = fullyPaid
                                    ? "Full project payment verified. You can now upload PM Surya Ghar documents."
                                    : "Your payment was verified and your request approved. You can now upload PM Surya Ghar documents - the remaining balance can be paid later.",
                                NotificationType = "StatusUpdate"
                            });
                        }
                    }
                }
            }

            // Spec: sirf success ka message dikhna chahiye. Totals, ₹20K minimum
            // aur stage-advance ki detail toast mein nahi jaati — wo sab page par
            // already dikhta hai (stat cards + Stage column) aur JSON fields mein
            // niche bhi bheja ja raha hai agar UI ko kabhi chahiye ho.
            var msg = "Payment verified.";

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

            return Json(new { success = true, message = "Payment rejected. User notified." });
        }
        catch (Exception ex)
        {
            var detail = ex.InnerException?.Message ?? ex.Message;
            return Json(new { success = false, message = $"Reject failed: {detail}" });
        }
    }

    // POST: /Admin/Payments/AddByAdmin
    //
    // Change request point 7 turned admin fund entry into two steps. This entry
    // point is kept because the "Add payment" modal on this page still posts to
    // it, but it no longer credits anything on its own: the money is queued as an
    // UNVERIFIED admin fund and a SECOND admin confirms it under
    // Funds → Approve Fund. The shared logic lives in IAdminFundService so this
    // and the Add Fund menu cannot drift apart.
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddByAdmin(int solarRequestId, decimal amount, string utrNumber,
        DateTime? paymentDate, string? referenceNumber, string? notes, IFormFile? receiptImage)
    {
        try
        {
            string? receiptPath = null;
            if (receiptImage != null && receiptImage.Length > 0)
            {
                var (ok, path, err) = await _fileUploadService.UploadAsync(receiptImage, "payments");
                if (!ok)
                    return Json(new { success = false, message = $"Receipt upload failed: {err}" });
                receiptPath = path;
            }

            var adminUser = await _userManager.GetUserAsync(User);

            var result = await _funds.AddAsync(new AddFundInput
            {
                SolarRequestId  = solarRequestId,
                Amount          = amount,
                UtrNumber       = utrNumber,
                PaymentDate     = paymentDate,
                ReferenceNumber = referenceNumber,
                Notes           = notes,
                ReceiptPath     = receiptPath,
                AdminId         = _userManager.GetUserId(User) ?? "system",
                AdminName       = adminUser?.FullName ?? adminUser?.UserName ?? "Admin"
            });

            if (!result.IsSuccess)
                return Json(new { success = false, message = result.Message ?? result.Errors.FirstOrDefault() });

            // stageAdvanced stays false by design — nothing advances until approval.
            return Json(new
            {
                success       = true,
                paymentId     = result.Data!.Id,
                stageAdvanced = false,
                message       = result.Message
            });
        }
        catch (Exception ex)
        {
            var detail = ex.InnerException?.Message ?? ex.Message;
            return Json(new { success = false, message = $"Add payment failed: {detail}" });
        }
    }
}
