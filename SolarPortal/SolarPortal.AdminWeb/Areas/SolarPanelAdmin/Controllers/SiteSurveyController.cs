using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using SolarPortal.Application.DTOs;
using SolarPortal.Application.Interfaces;
using SolarPortal.Application.Interfaces.Services;
using SolarPortal.Domain.Entities;
using SolarPortal.Domain.Enums;

namespace SolarPortal.AdminWeb.Areas.SolarPanelAdmin.Controllers;

/// <summary>
/// Admin reviews user-uploaded site surveys. On approval, project moves to MeterDispatch.
/// </summary>
[Area("SolarPanelAdmin")]
[Authorize(Roles = "Admin")]
public class SiteSurveyController : Controller
{
    private readonly IUnitOfWork _uow;
    private readonly ISolarRequestService _requestService;
    private readonly INotificationService _notifications;
    private readonly UserManager<ApplicationUser> _userManager;

    public SiteSurveyController(
        IUnitOfWork uow,
        ISolarRequestService requestService,
        INotificationService notifications,
        UserManager<ApplicationUser> userManager)
    {
        _uow = uow;
        _requestService = requestService;
        _notifications = notifications;
        _userManager = userManager;
    }

    // GET: /Admin/SiteSurvey
    public async Task<IActionResult> Index(string? filter)
    {
        var f = (filter ?? "pending").ToLowerInvariant();
        IEnumerable<SiteSurvey> surveys;

        if (f == "all")
        {
            surveys = (await _uow.SiteSurveys.GetAllAsync()).ToList();
        }
        else if (f == "approved")
        {
            surveys = (await _uow.SiteSurveys.FindAsync(s => s.IsCompleted)).ToList();
        }
        else // pending
        {
            surveys = (await _uow.SiteSurveys.FindAsync(s => !s.IsCompleted)).ToList();
        }

        var surveyList = surveys.ToList();
        var requestIds = surveyList.Select(s => s.SolarRequestId).Distinct().ToList();
        var requests = (await _uow.SolarRequests.GetAllAsync())
                       .Where(r => requestIds.Contains(r.Id))
                       .ToList();

        ViewBag.Surveys = surveyList;
        ViewBag.Filter = f;
        return View(requests);
    }

    // GET: /Admin/SiteSurvey/Details/5  — id is the survey id
    public async Task<IActionResult> Details(int id)
    {
        var survey = await _uow.SiteSurveys.GetByIdAsync(id);
        if (survey == null) return NotFound();
        var req = await _uow.SolarRequests.GetByIdAsync(survey.SolarRequestId);
        ViewBag.Request = req;
        return View(survey);
    }

    // POST: /Admin/SiteSurvey/Approve
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Approve(int surveyId, string? notes)
    {
        var survey = await _uow.SiteSurveys.GetByIdAsync(surveyId);
        if (survey == null) return Json(new { success = false, message = "Survey not found" });

        survey.IsCompleted = true;
        survey.CompletedAt = DateTime.UtcNow;
        if (!string.IsNullOrWhiteSpace(notes))
            survey.SurveyNotes = (survey.SurveyNotes ?? "") + $"\n[Admin] {notes}";
        _uow.SiteSurveys.Update(survey);
        await _uow.SaveChangesAsync();

        var adminId = _userManager.GetUserId(User)!;
        await _requestService.UpdateStageAsync(new UpdateSolarRequestStatusDto
        {
            Id = survey.SolarRequestId,
            NewStage = ProjectStatus.MaterialDispatch,
            Notes = "Site survey approved by admin."
        }, adminId);

        var req = await _uow.SolarRequests.GetByIdAsync(survey.SolarRequestId);
        if (req != null)
        {
            await _notifications.CreateAsync(new CreateNotificationDto
            {
                UserId = req.UserId,
                SolarRequestId = req.Id,
                Title = "Site Survey approved",
                Message = "Your site survey has been approved. Meter dispatch is next.",
                NotificationType = "SiteSurvey"
            });
        }

        return Json(new { success = true, message = "Site survey approved. Project moved to Meter Dispatch." });
    }

    // POST: /Admin/SiteSurvey/Reject
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Reject(int surveyId, string reason)
    {
        var survey = await _uow.SiteSurveys.GetByIdAsync(surveyId);
        if (survey == null) return Json(new { success = false, message = "Survey not found" });

        survey.SurveyNotes = (survey.SurveyNotes ?? "") + $"\n[Admin REJECTED] {reason}";
        _uow.SiteSurveys.Update(survey);
        await _uow.SaveChangesAsync();

        var req = await _uow.SolarRequests.GetByIdAsync(survey.SolarRequestId);
        if (req != null)
        {
            await _notifications.CreateAsync(new CreateNotificationDto
            {
                UserId = req.UserId,
                SolarRequestId = req.Id,
                Title = "Site Survey rejected",
                Message = $"Reason: {reason}. Please re-submit.",
                NotificationType = "SiteSurvey"
            });
        }

        return Json(new { success = true, message = "Site survey rejected. User notified." });
    }
}
