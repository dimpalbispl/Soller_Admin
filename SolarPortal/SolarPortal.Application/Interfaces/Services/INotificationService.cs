using SolarPortal.Application.DTOs;

namespace SolarPortal.Application.Interfaces.Services;

public interface INotificationService
{
    Task CreateAsync(CreateNotificationDto dto);
    Task<IEnumerable<NotificationDto>> GetUserNotificationsAsync(string userId);
    Task MarkAsReadAsync(int notificationId);
}