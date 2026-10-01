using SolarPortal.Domain.Common;

namespace SolarPortal.Domain.Entities;

/// <summary>Kind of entry an installer filed against one checklist item.</summary>
public enum ChecklistEntryType
{
    Video = 2,
    Remark = 3
}

/// <summary>
/// The video or written details an installer filed for one checklist item
/// (dbo.InstallationChecklistEntries). Photos are NOT kept here — they stay in
/// InstallationPhotos, tagged with FormatItemId.
///
/// Written by the INSTALLER panel (ADD-IncUploadFormat.sql creates the table);
/// the admin only reads it. Mirrors that panel's entity exactly.
/// </summary>
public class InstallationChecklistEntry : BaseEntity
{
    public int InstallationId { get; set; }

    /// <summary>Denormalised, like InstallationPhoto.</summary>
    public int SolarRequestId { get; set; }

    /// <summary>IncUploadFormats.Id this entry belongs to.</summary>
    public int FormatItemId { get; set; }

    public ChecklistEntryType EntryType { get; set; }

    // Video
    public string? FilePath { get; set; }
    public string? FileName { get; set; }
    public string? ContentType { get; set; }
    public long FileSizeBytes { get; set; }

    // Written details
    public string? RemarkText { get; set; }

    public int? UploadedByWorkerId { get; set; }
}
