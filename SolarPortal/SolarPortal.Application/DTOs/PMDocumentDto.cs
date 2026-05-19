using SolarPortal.Domain.Enums;

namespace SolarPortal.Application.DTOs;

public class PMDocumentDto
{
    public int Id { get; set; }
    public int SolarRequestId { get; set; }
    public DocumentType DocumentType { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public string? ContentType { get; set; }
    public long FileSize { get; set; }
    public ApprovalStatus Status { get; set; }
    public string? Remarks { get; set; }
    public DateTime CreatedAt { get; set; }
}