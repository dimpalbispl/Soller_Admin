using SolarPortal.Domain.Common;

namespace SolarPortal.Domain.Entities;

public class DCRDocument : BaseEntity
{
    public int SolarRequestId { get; set; }
    public string? DCRNumber { get; set; }
    public DateTime? DCRDate { get; set; }
    public string? DocumentPath { get; set; }
    public string? Remark { get; set; }
    public string? ExtractedData { get; set; } // JSON from OCR
    public bool IsVerified { get; set; } = false;

    public virtual SolarRequest? SolarRequest { get; set; }
}