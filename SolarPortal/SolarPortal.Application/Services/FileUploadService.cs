using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using SolarPortal.Application.Interfaces.Services;

namespace SolarPortal.Application.Services;

public class FileUploadService : IFileUploadService
{
    private readonly IWebHostEnvironment _env;
    private readonly IConfiguration _config;
    private readonly string[] _allowedExtensions = { ".jpg", ".jpeg", ".png", ".pdf" };
    private readonly long _maxFileSize = 10 * 1024 * 1024; // 10 MB

    public FileUploadService(IWebHostEnvironment env, IConfiguration config)
    {
        _env = env;
        _config = config;
    }

    public async Task<(bool Success, string? FilePath, string? Error)> UploadAsync(
        IFormFile file, string subfolder)
    {
        if (file == null || file.Length == 0)
            return (false, null, "No file provided");

        if (file.Length > _maxFileSize)
            return (false, null, "File exceeds 10MB limit");

        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (!_allowedExtensions.Contains(ext))
            return (false, null, "Invalid file type. Allowed: JPG, PNG, PDF");

        var uploadFolder = Path.Combine(_env.WebRootPath, "uploads", subfolder);
        Directory.CreateDirectory(uploadFolder);

        var uniqueName = $"{Guid.NewGuid()}{ext}";
        var fullPath = Path.Combine(uploadFolder, uniqueName);

        using var stream = new FileStream(fullPath, FileMode.Create);
        await file.CopyToAsync(stream);

        var relativePath = $"/uploads/{subfolder}/{uniqueName}";
        return (true, relativePath, null);
    }

    public void DeleteFile(string filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return;
        var fullPath = Path.Combine(_env.WebRootPath, filePath.TrimStart('/'));
        if (File.Exists(fullPath))
            File.Delete(fullPath);
    }
}