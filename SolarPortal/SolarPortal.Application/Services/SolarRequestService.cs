using AutoMapper;
using SolarPortal.Application.DTOs;
using SolarPortal.Application.Interfaces;
using SolarPortal.Application.Interfaces.Services;
using SolarPortal.Domain.Entities;
using SolarPortal.Domain.Enums;

namespace SolarPortal.Application.Services;

public class SolarRequestService : ISolarRequestService
{
    private readonly IUnitOfWork _uow;
    private readonly IMapper _mapper;
    private readonly INotificationService _notificationService;

    public SolarRequestService(IUnitOfWork uow, IMapper mapper, INotificationService notificationService)
    {
        _uow = uow;
        _mapper = mapper;
        _notificationService = notificationService;
    }

    public async Task<ServiceResult<SolarRequestDto>> CreateAsync(CreateSolarRequestDto dto, string userId)
    {
        try
        {
            var entity = _mapper.Map<SolarRequest>(dto);
            entity.UserId = userId;
            entity.RequestNumber = await _uow.SolarRequests.GenerateRequestNumberAsync();
            entity.CurrentStage = !string.IsNullOrEmpty(dto.SelectedPlan)
                ? ProjectStatus.Payment
                : ProjectStatus.Registration;
            entity.ApprovalStatus = ApprovalStatus.Pending;

            await _uow.SolarRequests.AddAsync(entity);
            await _uow.SaveChangesAsync();

            // Create welcome notification
            await _notificationService.CreateAsync(new CreateNotificationDto
            {
                UserId = userId,
                SolarRequestId = entity.Id,
                Title = "Application Submitted",
                Message = $"Your solar connection request {entity.RequestNumber} has been submitted successfully.",
                NotificationType = "StatusUpdate"
            });

            var result = _mapper.Map<SolarRequestDto>(entity);
            return ServiceResult<SolarRequestDto>.Success(result, "Request created successfully");
        }
        catch (Exception ex)
        {
            return ServiceResult<SolarRequestDto>.Failure($"Failed to create request: {ex.Message}");
        }
    }

    public async Task<ServiceResult<SolarRequestDto>> GetByIdAsync(int id)
    {
        var entity = await _uow.SolarRequests.GetByIdAsync(id);
        if (entity == null)
            return ServiceResult<SolarRequestDto>.Failure("Request not found");

        return ServiceResult<SolarRequestDto>.Success(_mapper.Map<SolarRequestDto>(entity));
    }

    public async Task<ServiceResult<SolarRequestDto>> GetByRequestNumberAsync(string requestNumber)
    {
        var entity = await _uow.SolarRequests.GetByRequestNumberAsync(requestNumber);
        if (entity == null)
            return ServiceResult<SolarRequestDto>.Failure("Request not found");

        return ServiceResult<SolarRequestDto>.Success(_mapper.Map<SolarRequestDto>(entity));
    }

    public async Task<ServiceResult<IEnumerable<SolarRequestDto>>> GetByUserIdAsync(string userId)
    {
        var entities = await _uow.SolarRequests.GetByUserIdAsync(userId);
        return ServiceResult<IEnumerable<SolarRequestDto>>.Success(_mapper.Map<IEnumerable<SolarRequestDto>>(entities));
    }

    public async Task<ServiceResult<IEnumerable<SolarRequestDto>>> GetAllAsync(int page = 1, int pageSize = 20)
    {
        var (items, _) = await _uow.SolarRequests.GetPagedAsync(page, pageSize,
            orderBy: q => q.OrderByDescending(x => x.CreatedAt),
            includeProperties: "User,Payments");

        // Per spec: jab tak user request actually submit na kare, uska auto-stub
        // (first-login placeholder) admin ki kisi bhi list / report mein nahi
        // dikhna chahiye. A stub has no plan/product, KV 0, amount 0, and isn't
        // completed. Filtering here means EVERY admin screen that calls GetAllAsync
        // (All Projects, workflow lists, dashboards) hides it automatically.
        var real = items.Where(r =>
            r.SolarProjectId != null ||
            r.ExternalProductId != null ||
            r.KVCapacity != 0m ||
            r.PlanAmount != 0m ||
            r.CurrentStage == ProjectStatus.Completed);

        return ServiceResult<IEnumerable<SolarRequestDto>>.Success(_mapper.Map<IEnumerable<SolarRequestDto>>(real));
    }

    public async Task<ServiceResult<IEnumerable<SolarRequestDto>>> GetPendingApprovalsAsync()
    {
        var entities = await _uow.SolarRequests.GetPendingApprovalsAsync();
        return ServiceResult<IEnumerable<SolarRequestDto>>.Success(_mapper.Map<IEnumerable<SolarRequestDto>>(entities));
    }

    public async Task<ServiceResult<bool>> ApproveAsync(int id, string adminId, string? notes = null)
    {
        var entity = await _uow.SolarRequests.GetByIdAsync(id);
        if (entity == null)
            return ServiceResult<bool>.Failure("Request not found");

        // ===== Idempotency guard =====
        // Repeated Approve/Reject calls on the same request used to silently toggle
        // state, which caused the IDNo-SE86372259 issue (admin clicked twice and the
        // record bounced between Approved/Rejected). Now we hard-fail any non-Pending
        // re-decision so the action is one-way until an admin explicitly resets it.
        if (entity.ApprovalStatus == ApprovalStatus.Approved)
            return ServiceResult<bool>.Failure(
                $"Request {entity.RequestNumber} is already approved. Duplicate approval not allowed.");

        if (entity.ApprovalStatus == ApprovalStatus.Rejected)
            return ServiceResult<bool>.Failure(
                $"Request {entity.RequestNumber} was rejected previously. " +
                "It cannot be approved again from this screen. Ask the user to re-submit.");

        // Per spec: pre-approval here only releases the request out of "Pending Request"
        // into the payment workflow. FINAL approval is gated by Payment Verification
        // and only happens when verified payments total ≥ ₹20,000 (handled in
        // PaymentsController.Verify). So we move CurrentStage to Payment, not all the
        // way to ProductSelection like before.
        entity.ApprovalStatus = ApprovalStatus.Approved;
        entity.AdminNotes = notes;
        if (entity.CurrentStage == ProjectStatus.Registration)
            entity.CurrentStage = ProjectStatus.Payment;
        entity.UpdatedAt = DateTime.UtcNow;

        _uow.SolarRequests.Update(entity);
        await _uow.SaveChangesAsync();

        await _notificationService.CreateAsync(new CreateNotificationDto
        {
            UserId = entity.UserId,
            SolarRequestId = entity.Id,
            Title = "Application Approved! ✅",
            Message = $"Your request {entity.RequestNumber} has been approved. " +
                      "Please complete the payment to move to PM Surya Ghar.",
            NotificationType = "StatusUpdate"
        });

        return ServiceResult<bool>.Success(true, "Request approved. User can now make payment.");
    }

    public async Task<ServiceResult<bool>> RejectAsync(int id, string adminId, string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return ServiceResult<bool>.Failure("Rejection reason is required.");

        var entity = await _uow.SolarRequests.GetByIdAsync(id);
        if (entity == null)
            return ServiceResult<bool>.Failure("Request not found");

        // ===== Idempotency guard (same rationale as ApproveAsync above) =====
        if (entity.ApprovalStatus == ApprovalStatus.Rejected)
            return ServiceResult<bool>.Failure(
                $"Request {entity.RequestNumber} is already rejected. Duplicate rejection not allowed.");

        if (entity.ApprovalStatus == ApprovalStatus.Approved)
            return ServiceResult<bool>.Failure(
                $"Request {entity.RequestNumber} was already approved. " +
                "Cannot reject an approved request from this screen.");

        entity.ApprovalStatus = ApprovalStatus.Rejected;
        entity.RejectionReason = reason;
        entity.UpdatedAt = DateTime.UtcNow;
        _uow.SolarRequests.Update(entity);
        await _uow.SaveChangesAsync();

        await _notificationService.CreateAsync(new CreateNotificationDto
        {
            UserId = entity.UserId,
            SolarRequestId = entity.Id,
            Title = "Application Rejected",
            Message = $"Your request {entity.RequestNumber} was rejected. Reason: {reason}",
            NotificationType = "StatusUpdate"
        });

        return ServiceResult<bool>.Success(true, "Request rejected");
    }

    public async Task<ServiceResult<bool>> UpdateStageAsync(UpdateSolarRequestStatusDto dto, string adminId)
    {
        var entity = await _uow.SolarRequests.GetByIdAsync(dto.Id);
        if (entity == null)
            return ServiceResult<bool>.Failure("Request not found");

        entity.CurrentStage = dto.NewStage;
        if (dto.Notes != null) entity.AdminNotes = dto.Notes;

        // When the workflow reaches its final step the project is, by definition,
        // approved — there is no separate manual approval action at the end. The
        // last stage differs by connection type:
        //   Domestic   → DCRUpdate then Completed
        //   Commercial → Installation then Completed
        // Either way, once CurrentStage == Completed we lock ApprovalStatus to
        // Approved so the admin Details / dashboard badges read correctly and the
        // Approve/Reject buttons disappear. Persisting it (not just a view patch)
        // keeps every screen consistent.
        if (dto.NewStage == ProjectStatus.Completed)
        {
            entity.ApprovalStatus = ApprovalStatus.Approved;
        }

        _uow.SolarRequests.Update(entity);
        await _uow.SaveChangesAsync();

        await _notificationService.CreateAsync(new CreateNotificationDto
        {
            UserId = entity.UserId,
            SolarRequestId = entity.Id,
            Title = $"Status Updated: {dto.NewStage}",
            Message = $"Your request {entity.RequestNumber} has moved to stage: {dto.NewStage.ToString().Replace("_", " ")}",
            NotificationType = "StatusUpdate"
        });

        return ServiceResult<bool>.Success(true, "Stage updated");
    }

    public async Task<ServiceResult<SolarRequestDto>> GetWithDetailsAsync(int id)
    {
        var entity = await _uow.SolarRequests.GetWithDetailsAsync(id);
        if (entity == null)
            return ServiceResult<SolarRequestDto>.Failure("Request not found");

        var dto = _mapper.Map<SolarRequestDto>(entity);
        dto.TotalPaid = entity.Payments.Where(p => p.IsVerified).Sum(p => p.Amount);
        dto.TotalDue = entity.PlanAmount - dto.TotalPaid;
        return ServiceResult<SolarRequestDto>.Success(dto);
    }
}