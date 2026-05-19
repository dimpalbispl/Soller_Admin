using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SolarPortal.Application.Interfaces;
using SolarPortal.Application.Interfaces.Services;
using SolarPortal.Application.Mappings;
using SolarPortal.Application.Services;
using SolarPortal.Domain.Entities;
using SolarPortal.Infrastructure.Data;
using SolarPortal.Infrastructure.Services;

namespace SolarPortal.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services, IConfiguration configuration)
    {
        // Database
        services.AddDbContext<ApplicationDbContext>(options =>
            options.UseSqlServer(
                configuration.GetConnectionString("DefaultConnection"),
                b => b.MigrationsAssembly(typeof(ApplicationDbContext).Assembly.FullName)));

        // Identity
        services.AddIdentity<ApplicationUser, IdentityRole>(options =>
        {
            // ── Relaxed rules: live DB users may have any-length plain-text
            //    passwords. We don't enforce complexity on the Identity side
            //    because m_xxxmaster is the source of truth. ──
            options.Password.RequireDigit = false;
            options.Password.RequiredLength = 1;
            options.Password.RequireNonAlphanumeric = false;
            options.Password.RequireUppercase = false;
            options.Password.RequireLowercase = false;
            options.SignIn.RequireConfirmedAccount = false;
            options.User.RequireUniqueEmail = true;

            // ── Lockout DISABLED. With lockout on, Identity calls
            //    Task.Delay() with the remaining lockout time. If that
            //    time is computed as negative (clock skew or a bridge-
            //    refreshed user), it throws ArgumentOutOfRangeException
            //    on the millisecondsDelay parameter. We don't want lockout
            //    anyway — m_membermaster / m_usermaster are the source of
            //    truth for credentials. ──
            options.Lockout.AllowedForNewUsers = false;
            options.Lockout.MaxFailedAccessAttempts = int.MaxValue;
            options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromSeconds(0);
        })
        .AddEntityFrameworkStores<ApplicationDbContext>()
        .AddDefaultTokenProviders();

        // Cookie config
        services.ConfigureApplicationCookie(options =>
        {
            options.LoginPath = "/Account/Login";
            options.AccessDeniedPath = "/Account/AccessDenied";
            options.ExpireTimeSpan = TimeSpan.FromHours(8);
            options.SlidingExpiration = true;
        });

        // Disable Identity's security-stamp revalidation timer.
        // The default revalidates every 30 minutes by computing a TimeSpan
        // that can flip negative under clock skew → Task.Delay throws
        // ArgumentOutOfRangeException(millisecondsDelay). Bumping it to a
        // very large interval (effectively turning it off) sidesteps the bug.
        services.Configure<Microsoft.AspNetCore.Identity.SecurityStampValidatorOptions>(o =>
        {
            o.ValidationInterval = TimeSpan.FromDays(7);
        });

        // Repositories & UoW
        services.AddScoped<IUnitOfWork, UnitOfWork>();

        // AutoMapper
        services.AddAutoMapper(typeof(MappingProfile).Assembly);

        // Services
        services.AddScoped<ISolarRequestService, SolarRequestService>();
        services.AddScoped<IDashboardService, DashboardService>();
        services.AddScoped<INotificationService, NotificationService>();
        services.AddScoped<IPaymentService, PaymentService>();
        services.AddScoped<ILiveDbAuthBridge, LiveDbAuthBridge>();
        services.AddScoped<IDocumentService, DocumentService>();
        services.AddScoped<IWorkerService, WorkerService>();
        services.AddScoped<IFileUploadService, FileUploadService>();
        services.AddScoped<ISolarProjectService, SolarProjectService>();
        services.AddScoped<ISolarAccountService, SolarAccountService>();
        services.AddScoped<IPMDocumentService, PMDocumentService>();
        services.AddScoped<IWalletService, WalletService>();
        services.AddScoped<IWithdrawalService, WithdrawalService>();

        return services;
    }
}