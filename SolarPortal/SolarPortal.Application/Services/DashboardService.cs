using AutoMapper;
using SolarPortal.Application.DTOs;
using SolarPortal.Application.Interfaces;
using SolarPortal.Application.Interfaces.Services;
using SolarPortal.Domain.Enums;

namespace SolarPortal.Application.Services;

public class DashboardService : IDashboardService
{
    private readonly IUnitOfWork _uow;
    private readonly IMapper _mapper;

    public DashboardService(IUnitOfWork uow, IMapper mapper)
    {
        _uow = uow;
        _mapper = mapper;
    }

    public async Task<AdminDashboardDto> GetAdminDashboardAsync()
    {
        var allRequests = (await _uow.SolarRequests.GetAllAsync()).ToList();
        var payments = await _uow.Payments.GetAllAsync();
        var workers = await _uow.Workers.GetAllAsync();
        var statusCounts = await _uow.SolarRequests.GetStatusCountsAsync();

        var recent = (await _uow.SolarRequests.GetPagedAsync(1, 10,
            orderBy: q => q.OrderByDescending(x => x.CreatedAt),
            includeProperties: "User,Payments")).Items;

        // === Filter out unfilled auto-stubs ===
        // Same logic as user-dashboard: an empty placeholder created at first
        // login shouldn't show up as a pending approval for the admin either —
        // the user hasn't submitted anything yet. We still count it under
        // TotalProjects since it represents an active account, but
        // PendingApprovals should reflect ACTUAL pending submissions.
        static bool IsUnfilledStub(SolarPortal.Domain.Entities.SolarRequest r) =>
            (r.CurrentStage == ProjectStatus.Registration ||
             r.CurrentStage == ProjectStatus.ProductSelection) &&
            r.ApprovalStatus == ApprovalStatus.Pending &&
            r.SolarProjectId == null &&
            r.ExternalProductId == null &&
            r.KVCapacity == 0m &&
            r.PlanAmount == 0m;

        var realRequests = allRequests.Where(r => !IsUnfilledStub(r)).ToList();

        return new AdminDashboardDto
        {
            TotalProjects = realRequests.Count,
            PendingApprovals = realRequests.Count(x => x.ApprovalStatus == ApprovalStatus.Pending),
            ActiveInstallations = realRequests.Count(x => x.CurrentStage == ProjectStatus.Installation),
            CompletedProjects = realRequests.Count(x => x.CurrentStage == ProjectStatus.Completed),
            TotalRevenue = payments.Where(p => p.IsVerified).Sum(p => p.Amount),
            PendingPayments = payments.Where(p => !p.IsVerified).Sum(p => p.Amount),
            TotalWorkers = workers.Count(),
            RecentRequests = _mapper.Map<List<SolarRequestDto>>(recent),
            StatusDistribution = statusCounts
        };
    }

    public async Task<UserDashboardDto> GetUserDashboardAsync(string userId)
    {
        var projects = (await _uow.SolarRequests.GetByUserIdAsync(userId)).ToList();
        var notifications = await _uow.Notifications.FindAsync(
            n => n.UserId == userId && !n.IsRead);

        // === Filter out unfilled auto-stubs ===
        // When a user first logs in, the system creates an empty placeholder
        // SolarRequest so the form flow has something to attach to. This stub
        // is in Registration/ProductSelection stage with Pending approval and
        // NO plan picked yet (SolarProjectId null, KV=0, PlanAmount=0). If we
        // count these, every brand-new user sees MY PROJECTS=1 and PENDING=1
        // on the dashboard before they've actually done anything — which is
        // the bug the user reported ("1 1 abhi bhi aa raha hai").
        //
        // Per spec: "Dashboard me default My Project = 1 & Pending Approval = 1
        // remove karo. Agar data nahi hai to 0 show ho."
        //
        // A stub is "real" once the user has touched it — either picked a
        // SolarProject, picked an ExternalProductId (basic product), set a
        // KV capacity, or entered a plan amount.
        static bool IsUnfilledStub(SolarPortal.Domain.Entities.SolarRequest r) =>
            (r.CurrentStage == ProjectStatus.Registration ||
             r.CurrentStage == ProjectStatus.ProductSelection) &&
            r.ApprovalStatus == ApprovalStatus.Pending &&
            r.SolarProjectId == null &&
            r.ExternalProductId == null &&
            r.KVCapacity == 0m &&
            r.PlanAmount == 0m;

        var realProjects = projects.Where(p => !IsUnfilledStub(p)).ToList();

        // Per spec: "Dashboard counts logged-in user ke actual data se show karo."
        // Math is defensive: due never goes negative; verified-only totals.
        var verifiedPaid = realProjects.SelectMany(p => p.Payments)
                                       .Where(pmt => pmt.IsVerified)
                                       .Sum(pmt => pmt.Amount);
        var totalPlanned = realProjects.Sum(p => p.PlanAmount);
        var totalDue     = Math.Max(0m, totalPlanned - verifiedPaid);

        return new UserDashboardDto
        {
            TotalProjects = realProjects.Count,
            // Per spec: Completed projects shouldn't count as pending approval —
            // a finished project is implicitly approved regardless of any stale
            // ApprovalStatus value on the entity.
            PendingApprovals = realProjects.Count(p =>
                p.ApprovalStatus == ApprovalStatus.Pending &&
                p.CurrentStage   != ProjectStatus.Completed),
            TotalPaid = verifiedPaid,
            TotalDue = totalDue,
            // MyProjects / LatestProject still includes the stub so the
            // "Next Action" card on the dashboard can show "Fill your request"
            // for new users. Counts above are what the user explicitly asked
            // to show 0 for brand-new accounts.
            MyProjects = _mapper.Map<List<SolarRequestDto>>(projects.Take(5)),
            LatestProject = projects.Any() ? _mapper.Map<SolarRequestDto>(projects.First()) : null,
            UnreadNotifications = _mapper.Map<List<NotificationDto>>(notifications.Take(5))
        };
    }
}