using SolarPortal.Domain.Enums;

namespace SolarPortal.Application.DTOs;

public class DocumentDto
{
    public int Id { get; set; }
    public int SolarRequestId { get; set; }
    public DocumentType DocumentType { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public string? OriginalFileName { get; set; }
    public long FileSizeBytes { get; set; }
    public string ContentType { get; set; } = string.Empty;
    public bool IsVerified { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class SaveDocumentDto
{
    public int SolarRequestId { get; set; }
    public string UserId { get; set; } = string.Empty;
    public DocumentType DocumentType { get; set; }
    public string FilePath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string? OriginalFileName { get; set; }
    public long FileSizeBytes { get; set; }
    public string ContentType { get; set; } = string.Empty;
}