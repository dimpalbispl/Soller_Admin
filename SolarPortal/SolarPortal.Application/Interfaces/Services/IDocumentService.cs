using SolarPortal.Application.DTOs;

namespace SolarPortal.Application.Interfaces.Services;

public interface IDocumentService
{
    Task SaveDocumentAsync(SaveDocumentDto dto);
    Task<IEnumerable<DocumentDto>> GetByRequestIdAsync(int requestId);
}