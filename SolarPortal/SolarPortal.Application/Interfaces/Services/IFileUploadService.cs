using Microsoft.AspNetCore.Http;

namespace SolarPortal.Application.Interfaces.Services;

public interface IFileUploadService
{
    Task<(bool Success, string? FilePath, string? Error)> UploadAsync(IFormFile file, string subfolder);
    void DeleteFile(string filePath);
}