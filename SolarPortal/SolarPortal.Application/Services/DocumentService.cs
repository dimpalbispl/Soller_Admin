using AutoMapper;
using SolarPortal.Application.DTOs;
using SolarPortal.Application.Interfaces;
using SolarPortal.Application.Interfaces.Services;
using SolarPortal.Domain.Entities;

namespace SolarPortal.Application.Services;

public class DocumentService : IDocumentService
{
    private readonly IUnitOfWork _uow;
    private readonly IMapper _mapper;

    public DocumentService(IUnitOfWork uow, IMapper mapper)
    {
        _uow = uow;
        _mapper = mapper;
    }

    public async Task SaveDocumentAsync(SaveDocumentDto dto)
    {
        var doc = new Document
        {
            SolarRequestId = dto.SolarRequestId,
            UserId = dto.UserId,
            DocumentType = dto.DocumentType,
            FilePath = dto.FilePath,
            FileName = dto.FileName,
            OriginalFileName = dto.OriginalFileName,
            FileSizeBytes = dto.FileSizeBytes,
            ContentType = dto.ContentType,
            IsVerified = false
        };
        await _uow.Documents.AddAsync(doc);
        await _uow.SaveChangesAsync();
    }

    public async Task<IEnumerable<DocumentDto>> GetByRequestIdAsync(int requestId)
    {
        var docs = await _uow.Documents.FindAsync(d => d.SolarRequestId == requestId);
        return _mapper.Map<IEnumerable<DocumentDto>>(docs);
    }
}