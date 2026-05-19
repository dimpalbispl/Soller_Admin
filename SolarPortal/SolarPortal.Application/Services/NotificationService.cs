using SolarPortal.Application.DTOs;
using SolarPortal.Application.Interfaces;
using SolarPortal.Application.Interfaces.Services;
using SolarPortal.Domain.Entities;

namespace SolarPortal.Application.Services;

public class NotificationService : INotificationService
{
    private readonly IUnitOfWork _uow;

    public NotificationService(IUnitOfWork uow) => _uow = uow;

    public async Task CreateAsync(CreateNotificationDto dto)
    {
        var notification = new Notification
        {
            UserId = dto.UserId,
            SolarRequestId = dto.SolarRequestId,
            Title = dto.Title,
            Message = dto.Message,
            NotificationType = dto.NotificationType
        };
        await _uow.Notifications.AddAsync(notification);
        await _uow.SaveChangesAsync();
    }

    public async Task<IEnumerable<NotificationDto>> GetUserNotificationsAsync(string userId)
    {
        var items = await _uow.Notifications.FindAsync(n => n.UserId == userId);
        return items.OrderByDescending(n => n.CreatedAt)
                    .Select(n => new NotificationDto
                    {
                        Id = n.Id,
                        Title = n.Title,
                        Message = n.Message,
                        IsRead = n.IsRead,
                        NotificationType = n.NotificationType,
                        CreatedAt = n.CreatedAt,
                        SolarRequestId = n.SolarRequestId
                    });
    }

    public async Task MarkAsReadAsync(int notificationId)
    {
        var notif = await _uow.Notifications.GetByIdAsync(notificationId);
        if (notif != null)
        {
            notif.IsRead = true;
            notif.ReadAt = DateTime.UtcNow;
            _uow.Notifications.Update(notif);
            await _uow.SaveChangesAsync();
        }
    }
}