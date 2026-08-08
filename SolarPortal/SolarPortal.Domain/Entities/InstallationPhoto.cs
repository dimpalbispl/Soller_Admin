using SolarPortal.Domain.Common;

namespace SolarPortal.Domain.Entities;

/// <summary>
/// One photo from an INC's "Mark Installed" submission. The spec allows up to
/// 30 per installation, which is why these are rows rather than more columns on
/// Installation — and why the admin reviews the SET
/// (<see cref="Installation.ApprovalStatus"/>) instead of each file.
///
/// SolarRequestId is denormalised alongside InstallationId so the admin queues,
/// which are built per request, don't have to join through Installations.
///
/// The INSTALLER panel writes these rows (ADD-UserPanelIncPoints.sql creates the
/// table); the admin only reads them. This class must therefore mirror that
/// panel's entity property-for-property — the shared DB has exactly one shape.
/// </summary>
public class InstallationPhoto : BaseEntity
{
    /// <summary>Hard cap from the spec — 30 photos per installation.</summary>
    public const int MaxPerInstallation = 30;

    public int InstallationId { get; set; }
    public int SolarRequestId { get; set; }

    public string FilePath { get; set; } = string.Empty;
    public string? FileName { get; set; }
    public string? ContentType { get; set; }

    /// <summary>Column is FileSizeBytes — NOT FileSize. Named by the installer panel.</summary>
    public long FileSizeBytes { get; set; }

    /// <summary>INC worker who uploaded it.</summary>
    public int? UploadedByWorkerId { get; set; }

    public virtual Installation? Installation { get; set; }
}
