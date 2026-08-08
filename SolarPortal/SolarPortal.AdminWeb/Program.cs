using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Authorization;
using Serilog;
using SolarPortal.Infrastructure;
using SolarPortal.Infrastructure.Data;

var builder = WebApplication.CreateBuilder(args);

// ─── Serilog ──────────────────────────────────────────────────────────────
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .WriteTo.Console()
    .WriteTo.File("Logs/solar-portal-admin-.txt", rollingInterval: RollingInterval.Day)
    .CreateLogger();

builder.Host.UseSerilog();

// ─── MVC with global auth policy ─────────────────────────────────────────
// Every non-Anonymous action requires auth. Anonymous controllers (like
// AccountController/Login) override this with [AllowAnonymous].
var mvcBuilder = builder.Services.AddControllersWithViews(options =>
{
    var policy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
    options.Filters.Add(new AuthorizeFilter(policy));

    // admin -> user permission grid. Opt-in per user: an admin with no rows
    // configured keeps full access, so turning this on locks nobody out.
    options.Filters.Add<SolarPortal.AdminWeb.Middleware.AdminPermissionFilter>();
});

if (builder.Environment.IsDevelopment())
{
    // Razor runtime compilation watches every .cshtml it compiles so edits show
    // up without a rebuild. Its default watcher is FileSystemWatcher, which
    // cannot open a directory handle when the project lives on a UNC / network
    // share (this repo runs from \\localhost\Sadhna\...). It then NREs inside
    // FileSystemWatcher.StartRaisingEvents(), and because the failure happens
    // while *locating a partial view*, the whole page dies with a bare
    // "Object reference not set to an instance of an object."
    //
    // Polling mode gives the same edit-and-refresh behaviour without ever
    // touching FileSystemWatcher, so it works on UNC paths.
    var contentRoot = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(
        builder.Environment.ContentRootPath)
    {
        UsePollingFileWatcher = true,
        UseActivePolling = true
    };

    mvcBuilder.AddRazorRuntimeCompilation(options =>
    {
        options.FileProviders.Clear();
        options.FileProviders.Add(contentRoot);
    });
}

// ─── Infrastructure (DB, Identity, Services — shared with other sites) ──
builder.Services.AddInfrastructure(builder.Configuration);

// ─── Session ─────────────────────────────────────────────────────────────
builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromMinutes(30);
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
    options.Cookie.Name = ".SolarPortal.Admin.Session";
});

// Distinct auth cookie so admin/user/inc sites don't collide on localhost
builder.Services.ConfigureApplicationCookie(options =>
{
    options.Cookie.Name = ".SolarPortal.Admin.Auth";
    options.LoginPath  = "/Account/Login";
    options.LogoutPath = "/Account/Logout";
    options.AccessDeniedPath = "/Account/AccessDenied";
});

var app = builder.Build();

// Per-environment exception handling. See User Panel Program.cs for full notes.
// In Development → DeveloperExceptionPage (full stack trace, source code).
// In Production  → custom ExceptionHandlingMiddleware (friendly page).
if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}
else
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();

// ─────────────────────────────────────────────────────────────────────
//  Cross-panel uploads serving
//  ----------------------------------------------------------------
//  Files (payment receipts, PM Surya docs, dispatch documents, site
//  survey photos, DCR docs) are physically saved into the USER panel's
//  wwwroot/uploads folder. When admin renders <img src="/uploads/...">
//  it looks in the ADMIN panel's wwwroot, where the file doesn't exist
//  → broken image.
//
//  Fix: map the user panel's uploads folder as an additional static
//  file root under the same URL prefix. Admin can keep using paths like
//  "/uploads/payments/abc.jpg" and they resolve to the shared folder.
//
//  The path is configurable via appsettings "SharedUploadsPath". If not
//  set, we fall back to a relative path that works for the standard
//  side-by-side layout used by this solution (admin and user panels
//  living under sibling folders).
// ─────────────────────────────────────────────────────────────────────
var sharedUploads = builder.Configuration["SharedUploadsPath"];
if (string.IsNullOrWhiteSpace(sharedUploads))
{
    // Default: look two levels up from admin's content root and find
    // the user panel's wwwroot. Layout:
    //   <root>/AdminPanel/Soller_Admin/Soller_Admin/SolarPortal/SolarPortal.AdminWeb
    //   <root>/UserPanel/SolarPortal/SolarPortal/SolarPortal.Web/wwwroot/uploads
    // We try a couple of likely relative paths and use whichever exists.
    string[] candidates = {
        Path.Combine(app.Environment.ContentRootPath, "..", "..", "..", "..", "..",
                     "UserPanel", "SolarPortal", "SolarPortal", "SolarPortal.Web", "wwwroot", "uploads"),
        Path.Combine(app.Environment.ContentRootPath, "..", "..", "..", "..",
                     "SolarPortal", "SolarPortal", "SolarPortal.Web", "wwwroot", "uploads"),
        Path.Combine(app.Environment.ContentRootPath, "..", "SolarPortal.Web", "wwwroot", "uploads"),
    };
    foreach (var c in candidates)
    {
        var full = Path.GetFullPath(c);
        if (Directory.Exists(full)) { sharedUploads = full; break; }
    }
}

if (!string.IsNullOrWhiteSpace(sharedUploads) && Directory.Exists(sharedUploads))
{
    app.UseStaticFiles(new Microsoft.AspNetCore.Builder.StaticFileOptions
    {
        FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(sharedUploads),
        RequestPath = "/uploads"
    });
    // Log for diagnostics
    Console.WriteLine($"[Admin] Mapped shared uploads from: {sharedUploads}");
}
else
{
    Console.WriteLine("[Admin] WARNING: SharedUploadsPath not configured and no candidate folder found. " +
                      "Uploaded user files (payment receipts, PM Surya docs, dispatch images) may not display.");
}

app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();
app.UseSession();

// Custom error handling middleware — only in Production.
if (!app.Environment.IsDevelopment())
{
    app.UseMiddleware<SolarPortal.AdminWeb.Middleware.ExceptionHandlingMiddleware>();
}

app.MapControllerRoute(
    name: "areas",
    pattern: "{area:exists}/{controller=Dashboard}/{action=Index}/{id?}");

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Account}/{action=Login}/{id?}");

// Every admin controller lives in the SolarPanelAdmin area, so a typed or
// bookmarked URL without the area prefix — "/Dashboard/Index" — falls through
// the default route, finds no root DashboardController, and dies as a 404.
// Redirect those into the area instead of showing a dead page.
//
// The controller list is read from the routing table at startup rather than
// hardcoded, so a new area controller is covered automatically. Only names that
// really exist in the area are redirected — every other unknown URL still 404s,
// so genuine mistakes are not masked.
var areaControllerNames = new HashSet<string>(
    app.Services
       .GetRequiredService<Microsoft.AspNetCore.Mvc.Infrastructure.IActionDescriptorCollectionProvider>()
       .ActionDescriptors.Items
       .OfType<Microsoft.AspNetCore.Mvc.Controllers.ControllerActionDescriptor>()
       .Where(d => d.RouteValues.TryGetValue("area", out var a) &&
                   string.Equals(a, "SolarPanelAdmin", StringComparison.OrdinalIgnoreCase))
       .Select(d => d.ControllerName),
    StringComparer.OrdinalIgnoreCase);

app.MapFallback(context =>
{
    var segments = context.Request.Path.Value?
        .Split('/', StringSplitOptions.RemoveEmptyEntries) ?? Array.Empty<string>();

    if (segments.Length > 0 && areaControllerNames.Contains(segments[0]))
    {
        context.Response.Redirect(
            "/SolarPanelAdmin/" + string.Join('/', segments) + context.Request.QueryString);
        return Task.CompletedTask;
    }

    context.Response.StatusCode = StatusCodes.Status404NotFound;
    return Task.CompletedTask;
});

// Seed (shared DB — safe to run from every site, idempotent)
using (var scope = app.Services.CreateScope())
{
    var seeder = new DbSeeder(scope.ServiceProvider);
    await seeder.SeedAsync();
}

app.Run();
