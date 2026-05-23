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

    // Cleans a caller-supplied subfolder like "SCR-001/dcr" into a safe relative
    // path under /uploads. Each segment keeps only [A-Za-z0-9-_]; empty or unsafe
    // segments (".", "..") are dropped. Segments are re-joined with "/".
    private static string SanitizeSubfolder(string? subfolder)
    {
        if (string.IsNullOrWhiteSpace(subfolder)) return "misc";
        var segments = subfolder
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(seg =>
            {
                var clean = new string(seg.Where(c =>
                    char.IsLetterOrDigit(c) || c == '-' || c == '_').ToArray());
                return clean;
            })
            .Where(seg => seg.Length > 0 && seg != "." && seg != "..")
            .ToArray();
        return segments.Length > 0 ? string.Join("/", segments) : "misc";
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

        // Sanitise the subfolder. Callers pass things like "SCR-001/dcr" to
        // organise files per project. We allow letters, digits, dash, underscore
        // and the path separator between segments; everything else (spaces, "..",
        // drive letters, stray slashes) is stripped so we never escape /uploads.
        subfolder = SanitizeSubfolder(subfolder);

        var uploadFolder = Path.Combine(_env.WebRootPath, "uploads", subfolder);
        Directory.CreateDirectory(uploadFolder);

        var uniqueName = $"{Guid.NewGuid()}{ext}";
        var fullPath = Path.Combine(uploadFolder, uniqueName);

        using (var stream = new FileStream(fullPath, FileMode.Create))
        {
            await file.CopyToAsync(stream);
        }

        var relativePath = $"/uploads/{subfolder}/{uniqueName}";

        // ─── Mirror the file into the ADMIN panel's wwwroot ──────────────────
        // The admin panel is a separate app with its own wwwroot. When the user
        // uploads a receipt / document here, the admin can't see it unless the
        // file also lives under admin's wwwroot/uploads. So we copy it there too,
        // keeping the SAME relative path ("/uploads/<subfolder>/<file>"). This
        // means a plain <img src="/uploads/..."> works on BOTH panels — no
        // cross-panel static mapping or special controller needed.
        //
        // The admin wwwroot is found via:
        //   1. config "AdminUploadsMirrorPath"  (explicit wwwroot/uploads path)
        //   2. auto-probing common sibling folder layouts
        // If none is found, we silently skip the mirror — the user-side file is
        // still saved, and the admin FileController fallback can still resolve it.
        try
        {
            var adminUploadsRoot = ResolveAdminUploadsRoot();
            if (!string.IsNullOrWhiteSpace(adminUploadsRoot))
            {
                var adminFolder = Path.Combine(adminUploadsRoot, subfolder);
                Directory.CreateDirectory(adminFolder);
                var adminFullPath = Path.Combine(adminFolder, uniqueName);
                File.Copy(fullPath, adminFullPath, overwrite: true);
            }
        }
        catch
        {
            // Non-fatal: the user-side save succeeded. Admin display can still
            // fall back to the FileController that probes the user wwwroot.
        }

        return (true, relativePath, null);
    }

    // Locate the admin panel's wwwroot/uploads folder so uploaded files can be
    // mirrored there. Returns null if it can't be found.
    private string? ResolveAdminUploadsRoot()
    {
        // 1. Explicit config wins.
        var configured = _config["AdminUploadsMirrorPath"];
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;

        // 2. Probe common layouts relative to THIS (user) app's content root.
        //    Typical side-by-side dev layout:
        //      <root>/UserPanel/SolarPortal/SolarPortal/SolarPortal.Web        (this app)
        //      <root>/AdminPanel/Soller_Admin/Soller_Admin/SolarPortal/SolarPortal.AdminWeb/wwwroot/uploads
        var contentRoot = _env.ContentRootPath;
        string[] candidates =
        {
            Path.Combine(contentRoot, "..", "..", "..", "..", "..",
                         "AdminPanel", "Soller_Admin", "Soller_Admin", "SolarPortal",
                         "SolarPortal.AdminWeb", "wwwroot", "uploads"),
            Path.Combine(contentRoot, "..", "..", "..", "..",
                         "Soller_Admin", "Soller_Admin", "SolarPortal",
                         "SolarPortal.AdminWeb", "wwwroot", "uploads"),
            Path.Combine(contentRoot, "..", "SolarPortal.AdminWeb", "wwwroot", "uploads"),
        };
        foreach (var c in candidates)
        {
            try
            {
                var full = Path.GetFullPath(c);
                // The admin wwwroot itself must exist (its parent dir). We create
                // the uploads subfolder if needed, but only if the wwwroot is real
                // — we don't want to fabricate a wrong directory tree.
                var wwwroot = Directory.GetParent(full)?.FullName;
                if (wwwroot != null && Directory.Exists(wwwroot))
                    return full;
            }
            catch { /* ignore bad candidate */ }
        }
        return null;
    }

    public void DeleteFile(string filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return;
        var fullPath = Path.Combine(_env.WebRootPath, filePath.TrimStart('/'));
        if (File.Exists(fullPath))
            File.Delete(fullPath);

        // Also remove the mirrored copy from the admin wwwroot if present.
        try
        {
            var adminUploadsRoot = ResolveAdminUploadsRoot();
            if (!string.IsNullOrWhiteSpace(adminUploadsRoot))
            {
                // filePath is "/uploads/<subfolder>/<file>"; strip the leading
                // "/uploads/" to get "<subfolder>/<file>" relative to admin uploads.
                var rel = filePath.Replace("\\", "/").TrimStart('/');
                if (rel.StartsWith("uploads/", StringComparison.OrdinalIgnoreCase))
                    rel = rel.Substring("uploads/".Length);
                var adminFullPath = Path.Combine(adminUploadsRoot, rel.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(adminFullPath))
                    File.Delete(adminFullPath);
            }
        }
        catch { /* non-fatal */ }
    }
}
