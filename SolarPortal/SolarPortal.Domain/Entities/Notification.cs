using SolarPortal.Domain.Common;

namespace SolarPortal.Domain.Entities;

public class Notification : BaseEntity
{
    public string UserId { get; set; } = string.Empty;
    public int? SolarRequestId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public bool IsRead { get; set; } = false;
    public DateTime? ReadAt { get; set; }
    public string? NotificationType { get; set; } // StatusUpdate, Payment, Alert

    public virtual ApplicationUser? User { get; set; }
    public virtual SolarRequest? SolarRequest { get; set; }
}