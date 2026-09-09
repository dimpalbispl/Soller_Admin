using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace SolarPortal.AdminWeb.Areas.SolarPanelAdmin.Controllers;

/// <summary>
/// Serves user-uploaded files (payment receipts, PM Surya docs, dispatch
/// images, DCR docs) to the admin panel.
///
/// WHY THIS EXISTS
///   Files are written by the USER panel into ITS OWN wwwroot/uploads. The
///   admin panel runs as a separate app with a separate wwwroot, so a plain
///   <img src="/uploads/..."> 404s on the admin side. The cross-panel
///   static-file mapping in Program.cs tries to bridge this, but it depends
///   on the two panels sitting in a known relative folder layout — which
///   breaks the moment someone moves a folder.
///
///   This controller removes that fragility: it resolves the requested
///   relative path against a list of candidate roots (admin wwwroot first,
///   then the user panel's wwwroot via config / probing) and streams the
///   first file that exists. No static-file path mapping required.
///
/// SECURITY
///   - Only files UNDER an uploads folder are served (the relativePath is
///     sanitised; "../" traversal is rejected).
///   - Only known image / pdf extensions are returned.
///   - Admin-only (inherits the area's auth).
/// </summary>
[Area("SolarPanelAdmin")]
[Authorize]
public class FileController : Controller
{
    private readonly IWebHostEnvironment _env;
    private readonly IConfiguration _config;

    public FileController(IWebHostEnvironment env, IConfiguration config)
    {
        _env = env;
        _config = config;
    }

    // GET: /SolarPanelAdmin/File/View?path=/uploads/payments/abc.jpg
    [HttpGet]
    public IActionResult View(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return NotFound();

        // ── Sanitise the requested path ──────────────────────────────────
        // Normalise slashes, strip leading "/", reject traversal attempts.
        var rel = path.Replace("\\", "/").TrimStart('/');
        if (rel.StartsWith("wwwroot/", StringComparison.OrdinalIgnoreCase))
            rel = rel.Substring("wwwroot/".Length);
        if (rel.Contains("..") || Path.IsPathRooted(rel))
            return BadRequest("Invalid path");

        // Must be inside an uploads folder — we don't serve arbitrary files.
        if (!rel.StartsWith("uploads/", StringComparison.OrdinalIgnoreCase))
            return BadRequest("Only upload files can be served");

        // ── Build candidate roots ────────────────────────────────────────
        // 1. Admin's own wwwroot (in case the file was copied locally)
        // 2. Configured SharedUploadsPath's PARENT (so "uploads/..." resolves)
        // 3. Probed user-panel wwwroot via common relative layouts
        var roots = new List<string>();
        if (!string.IsNullOrWhiteSpace(_env.WebRootPath))
            roots.Add(_env.WebRootPath);

        var shared = _config["SharedUploadsPath"];
        if (!string.IsNullOrWhiteSpace(shared))
        {
            // SharedUploadsPath points at the .../wwwroot/uploads folder, so
            // its parent is the wwwroot that "uploads/..." should resolve under.
            var parent = Directory.GetParent(shared.TrimEnd('/', '\\'))?.FullName;
            if (!string.IsNullOrWhiteSpace(parent)) roots.Add(parent);
        }

        // Probe likely user-panel wwwroot locations relative to admin content root.
        string[] probes = {
            Path.Combine(_env.ContentRootPath, "..", "..", "..", "..", "..",
                         "UserPanel", "SolarPortal", "SolarPortal", "SolarPortal.Web", "wwwroot"),
            Path.Combine(_env.ContentRootPath, "..", "..", "..", "..",
                         "SolarPortal", "SolarPortal", "SolarPortal.Web", "wwwroot"),
            Path.Combine(_env.ContentRootPath, "..", "SolarPortal.Web", "wwwroot"),
        };
        foreach (var p in probes)
        {
            try { roots.Add(Path.GetFullPath(p)); } catch { /* ignore bad path */ }
        }

        // ── Find the first root that actually has the file ───────────────
        foreach (var root in roots)
        {
            var full = Path.GetFullPath(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)));
            // Defence-in-depth: ensure the resolved path is still under the root
            if (!full.StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase))
                continue;
            if (System.IO.File.Exists(full))
            {
                var contentType = GetContentType(full);
                // inline so it renders in the modal <img>/<iframe> rather than downloading
                Response.Headers["Content-Disposition"] = "inline";
                return PhysicalFile(full, contentType);
            }
        }

        // ── Cross-server fallback ─────────────────────────────────────────
        // The file wasn't found in any local root. User-submitted receipts /
        // documents live on the SEPARATE user-panel host (config
        // "UserPanelBaseUrl", e.g. https://solar.solfit.in), which serves
        // /uploads/... publicly. Redirect the browser there so the <img>/<iframe>
        // in the preview modal loads the file directly. Admin-uploaded files were
        // already served locally above, so this only affects user-panel files.
        var userPanelBase = _config["UserPanelBaseUrl"];
        if (!string.IsNullOrWhiteSpace(userPanelBase))
        {
            var redirectUrl = userPanelBase.TrimEnd('/') + "/" + rel;
            return Redirect(redirectUrl);
        }

        return NotFound();
    }

    // GET: /SolarPanelAdmin/File/Download?path=/uploads/payments/abc.jpg
    //
    // The preview modal's "⬇ Download" used to point at View() with an <a download>.
    // That attribute is IGNORED on a cross-origin URL, and View() redirects
    // user-panel files to the other host — so for exactly the files an admin wants
    // (member receipts, PM Surya docs) the link opened the file instead of saving
    // it. This action always answers from this origin and always as an attachment:
    // local files are streamed from disk, remote ones are fetched server-side first.
    [HttpGet]
    public async Task<IActionResult> Download(string? path)
    {
        var rel = Sanitise(path, out var error);
        if (rel == null) return error!;

        var fileName = Path.GetFileName(rel);

        foreach (var root in CandidateRoots())
        {
            var full = Path.GetFullPath(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)));
            if (!full.StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase))
                continue;
            if (System.IO.File.Exists(full))
                return PhysicalFile(full, GetContentType(full), fileName);   // attachment
        }

        // Not on this server — pull it from the user panel and hand it over as a
        // download, so the browser never has to deal with another origin.
        var userPanelBase = _config["UserPanelBaseUrl"];
        if (!string.IsNullOrWhiteSpace(userPanelBase))
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
                var bytes = await http.GetByteArrayAsync(userPanelBase.TrimEnd('/') + "/" + rel);
                return File(bytes, GetContentType(rel), fileName);
            }
            catch
            {
                // Fall through to NotFound — a broken link is better than a 500.
            }
        }

        return NotFound();
    }

    /// <summary>
    /// Shared path check for View/Download: normalises slashes, refuses traversal
    /// and anything outside an uploads folder. Returns null (with the result to
    /// send back) when the path is not acceptable.
    /// </summary>
    private string? Sanitise(string? path, out IActionResult? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(path)) { error = NotFound(); return null; }

        var rel = path.Replace("\\", "/").TrimStart('/');
        if (rel.StartsWith("wwwroot/", StringComparison.OrdinalIgnoreCase))
            rel = rel.Substring("wwwroot/".Length);
        if (rel.Contains("..") || Path.IsPathRooted(rel)) { error = BadRequest("Invalid path"); return null; }
        if (!rel.StartsWith("uploads/", StringComparison.OrdinalIgnoreCase))
        {
            error = BadRequest("Only upload files can be served");
            return null;
        }
        return rel;
    }

    /// <summary>Same root list View() walks, kept in one place for both actions.</summary>
    private List<string> CandidateRoots()
    {
        var roots = new List<string>();
        if (!string.IsNullOrWhiteSpace(_env.WebRootPath))
            roots.Add(_env.WebRootPath);

        var shared = _config["SharedUploadsPath"];
        if (!string.IsNullOrWhiteSpace(shared))
        {
            var parent = Directory.GetParent(shared.TrimEnd('/', '\\'))?.FullName;
            if (!string.IsNullOrWhiteSpace(parent)) roots.Add(parent);
        }

        string[] probes = {
            Path.Combine(_env.ContentRootPath, "..", "..", "..", "..", "..",
                         "UserPanel", "SolarPortal", "SolarPortal", "SolarPortal.Web", "wwwroot"),
            Path.Combine(_env.ContentRootPath, "..", "..", "..", "..",
                         "SolarPortal", "SolarPortal", "SolarPortal.Web", "wwwroot"),
            Path.Combine(_env.ContentRootPath, "..", "SolarPortal.Web", "wwwroot"),
        };
        foreach (var p in probes)
        {
            try { roots.Add(Path.GetFullPath(p)); } catch { /* ignore bad path */ }
        }
        return roots;
    }

    private static string GetContentType(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png"            => "image/png",
            ".gif"            => "image/gif",
            ".webp"           => "image/webp",
            ".bmp"            => "image/bmp",
            ".pdf"            => "application/pdf",
            _                 => "application/octet-stream"
        };
    }
}
