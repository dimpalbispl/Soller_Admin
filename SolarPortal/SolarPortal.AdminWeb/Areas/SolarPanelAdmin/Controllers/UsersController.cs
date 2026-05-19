using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SolarPortal.Application.DTOs;
using SolarPortal.Application.Interfaces.Services;
using SolarPortal.Domain.Entities;

namespace SolarPortal.AdminWeb.Areas.SolarPanelAdmin.Controllers;

/// <summary>
/// Admin User Management — list users, approve/reject pending registrations,
/// toggle active status. Per spec, new registrations require admin approval.
/// </summary>
[Area("SolarPanelAdmin")]
[Authorize(Roles = "Admin")]
public class UsersController : Controller
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly INotificationService _notifications;

    public UsersController(UserManager<ApplicationUser> userManager, INotificationService notifications)
    {
        _userManager = userManager;
        _notifications = notifications;
    }

    // GET: /Admin/Users
    public async Task<IActionResult> Index(string? filter)
    {
        var query = _userManager.Users.AsQueryable();
        if (filter == "pending") query = query.Where(u => !u.IsActive);
        if (filter == "active") query = query.Where(u => u.IsActive);

        var users = await query.OrderByDescending(u => u.CreatedAt).ToListAsync();

        // Augment with roles
        var rows = new List<UserRow>();
        foreach (var u in users)
        {
            var roles = await _userManager.GetRolesAsync(u);
            rows.Add(new UserRow(u, string.Join(", ", roles)));
        }

        ViewBag.Filter = filter ?? "all";
        ViewBag.PendingCount = await _userManager.Users.CountAsync(u => !u.IsActive);
        return View(rows);
    }

    // POST: /Admin/Users/Approve/{id}
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Approve(string id)
    {
        var user = await _userManager.FindByIdAsync(id);
        if (user == null) return Json(new { success = false, message = "User not found" });

        user.IsActive = true;
        user.EmailConfirmed = true;
        var result = await _userManager.UpdateAsync(user);
        if (!result.Succeeded)
            return Json(new { success = false, message = string.Join("; ", result.Errors.Select(e => e.Description)) });

        await _notifications.CreateAsync(new CreateNotificationDto
        {
            UserId = user.Id,
            Title = "Account approved",
            Message = "Welcome! Your account has been activated. You can now apply for a solar connection.",
            NotificationType = "Account"
        });

        return Json(new { success = true, message = "User approved." });
    }

    // POST: /Admin/Users/Reject/{id}
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Reject(string id, string? reason)
    {
        var user = await _userManager.FindByIdAsync(id);
        if (user == null) return Json(new { success = false, message = "User not found" });

        user.IsActive = false;
        var result = await _userManager.UpdateAsync(user);
        if (!result.Succeeded)
            return Json(new { success = false, message = string.Join("; ", result.Errors.Select(e => e.Description)) });

        await _notifications.CreateAsync(new CreateNotificationDto
        {
            UserId = user.Id,
            Title = "Account rejected",
            Message = $"Your registration was rejected. {reason}",
            NotificationType = "Account"
        });

        return Json(new { success = true, message = "User rejected." });
    }

    // POST: /Admin/Users/ToggleActive/{id}
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ToggleActive(string id)
    {
        var user = await _userManager.FindByIdAsync(id);
        if (user == null) return Json(new { success = false, message = "User not found" });

        user.IsActive = !user.IsActive;
        await _userManager.UpdateAsync(user);
        return Json(new { success = true, message = user.IsActive ? "Activated" : "Deactivated" });
    }

    public record UserRow(ApplicationUser User, string Roles);
}
