using SolarPortal.Domain.Common;

namespace SolarPortal.Domain.Entities;

/// <summary>
/// One item of the fixed 13-item installation checklist (dbo.IncUploadFormats).
///
/// The table is created and seeded by the INSTALLER panel's ADD-IncUploadFormat.sql
/// and is shared with that panel, so this class mirrors its entity exactly. The
/// rules an installation must meet per item live in InstallationChecklist.
/// </summary>
public class IncUploadFormat : BaseEntity
{
    /// <summary>Item number 1–13 — also the display order.</summary>
    public int SrNo { get; set; }

    /// <summary>Item text, e.g. "Serial numbers of all panels".</summary>
    public string Work { get; set; } = string.Empty;

    /// <summary>"Photo" column of the sheet (informational).</summary>
    public bool PhotoRequired { get; set; }

    /// <summary>Videos this item needs (0 = none).</summary>
    public int VideoCount { get; set; }

    public int MinPhotos { get; set; }

    /// <summary>0 = this item takes no photos.</summary>
    public int MaxPhotos { get; set; }

    /// <summary>The installer must type written details for this item.</summary>
    public bool RemarkRequired { get; set; }

    public bool IsActive { get; set; } = true;
}
