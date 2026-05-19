using SolarPortal.Application.DTOs;
using SolarPortal.Domain.Entities;
using SolarPortal.Domain.Enums;

namespace SolarPortal.Application.Interfaces.Services;

public interface IPMDocumentService
{
    Task<PMDocumentDto?> GetByIdAsync(int id);
    Task<IEnumerable<PMDocumentDto>> GetByRequestIdAsync(int requestId);
    Task<PMDocumentDto> UploadDocumentAsync(int solarRequestId, DocumentType documentType, string fileName, string filePath, string? contentType, long fileSize);
    Task ApproveDocumentAsync(int id, string? remarks);
    Task RejectDocumentAsync(int id, string? remarks);
    Task DeleteDocumentAsync(int id);
}