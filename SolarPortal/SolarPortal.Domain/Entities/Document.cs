using SolarPortal.Domain.Common;
using SolarPortal.Domain.Enums;

namespace SolarPortal.Domain.Entities;

public class Document : BaseEntity
{
    public int SolarRequestId { get; set; }
    public string UserId { get; set; } = string.Empty;
    public DocumentType DocumentType { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public string? OriginalFileName { get; set; }
    public long FileSizeBytes { get; set; }
    public string ContentType { get; set; } = string.Empty;
    public bool IsVerified { get; set; } = false;

    // OCR extracted data
    public string? OCRExtractedData { get; set; } // JSON string

    public virtual SolarRequest? SolarRequest { get; set; }
}