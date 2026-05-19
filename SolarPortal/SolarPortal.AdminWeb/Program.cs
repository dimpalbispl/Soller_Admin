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

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();
app.UseSession();

app.UseMiddleware<SolarPortal.AdminWeb.Middleware.ExceptionHandlingMiddleware>();

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
