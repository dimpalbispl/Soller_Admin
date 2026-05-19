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
        var allRequests = await _uow.SolarRequests.GetAllAsync();
        var payments = await _uow.Payments.GetAllAsync();
        var workers = await _uow.Workers.GetAllAsync();
        var statusCounts = await _uow.SolarRequests.GetStatusCountsAsync();

        var recent = (await _uow.SolarRequests.GetPagedAsync(1, 10,
            orderBy: q => q.OrderByDescending(x => x.CreatedAt),
            includeProperties: "User,Payments")).Items;

        return new AdminDashboardDto
        {
            TotalProjects = allRequests.Count(),
            PendingApprovals = allRequests.Count(x => x.ApprovalStatus == ApprovalStatus.Pending),
            ActiveInstallations = allRequests.Count(x => x.CurrentStage == ProjectStatus.Installation),
            CompletedProjects = allRequests.Count(x => x.CurrentStage == ProjectStatus.Completed),
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

        return new UserDashboardDto
        {
            TotalProjects = projects.Count,
            PendingApprovals = projects.Count(p => p.ApprovalStatus == ApprovalStatus.Pending),
            TotalPaid = projects.SelectMany(p => p.Payments).Where(p => p.IsVerified).Sum(p => p.Amount),
            TotalDue = projects.Sum(p => p.PlanAmount) -
                       projects.SelectMany(p => p.Payments).Where(p => p.IsVerified).Sum(p => p.Amount),
            MyProjects = _mapper.Map<List<SolarRequestDto>>(projects.Take(5)),
            LatestProject = projects.Any() ? _mapper.Map<SolarRequestDto>(projects.First()) : null,
            UnreadNotifications = _mapper.Map<List<NotificationDto>>(notifications.Take(5))
        };
    }
}