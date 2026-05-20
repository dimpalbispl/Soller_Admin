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
});

if (builder.Environment.IsDevelopment())
    mvcBuilder.AddRazorRuntimeCompilation();

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

// Seed (shared DB — safe to run from every site, idempotent)
using (var scope = app.Services.CreateScope())
{
    var seeder = new DbSeeder(scope.ServiceProvider);
    await seeder.SeedAsync();
}

app.Run();
