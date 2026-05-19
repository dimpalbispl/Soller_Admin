using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using SolarPortal.Domain.Entities;

namespace SolarPortal.Infrastructure.Data;

public class ApplicationDbContext : IdentityDbContext<ApplicationUser>
{
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
        : base(options) { }

    public DbSet<SolarRequest> SolarRequests => Set<SolarRequest>();
    public DbSet<Payment> Payments => Set<Payment>();

    // ─── READ-ONLY live DB tables (existing — never altered by migrations) ──
    public DbSet<MMemberMaster> Members => Set<MMemberMaster>();
    public DbSet<MUserMaster> AdminUsers => Set<MUserMaster>();
    public DbSet<MStateDivMaster> States => Set<MStateDivMaster>();
    public DbSet<Document> Documents => Set<Document>();
    public DbSet<SiteSurvey> SiteSurveys => Set<SiteSurvey>();
    public DbSet<MeterDispatch> MeterDispatches => Set<MeterDispatch>();
    public DbSet<MaterialDispatch> MaterialDispatches => Set<MaterialDispatch>();
    public DbSet<Installation> Installations => Set<Installation>();
    public DbSet<DCRDocument> DCRDocuments => Set<DCRDocument>();
    public DbSet<Worker> Workers => Set<Worker>();
    public DbSet<WorkerAssignment> WorkerAssignments => Set<WorkerAssignment>();
    public DbSet<Commission> Commissions => Set<Commission>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<SolarProject> SolarProjects => Set<SolarProject>();
    public DbSet<SolarAccount> SolarAccounts => Set<SolarAccount>();
    public DbSet<PMDocument> PMDocuments => Set<PMDocument>();
    public DbSet<Wallet> Wallets => Set<Wallet>();
    public DbSet<WalletTransaction> WalletTransactions => Set<WalletTransaction>();
    public DbSet<Withdrawal> Withdrawals => Set<Withdrawal>();
    public DbSet<ActivityLog> ActivityLogs => Set<ActivityLog>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        // ─── Live DB read-only tables — EF will NEVER generate ALTER/CREATE
        //     /DROP statements for these. They exist as queryable entities
        //     only so we can authenticate against them and read state data. ──
        builder.Entity<MMemberMaster>(e =>
        {
            e.ToTable("m_membermaster", t => t.ExcludeFromMigrations());
            e.HasKey(x => x.MId);
        });
        builder.Entity<MUserMaster>(e =>
        {
            e.ToTable("m_usermaster", t => t.ExcludeFromMigrations());
            e.HasKey(x => x.UId);
        });
        builder.Entity<MStateDivMaster>(e =>
        {
            e.ToTable("m_statedivmaster", t => t.ExcludeFromMigrations());
            e.HasKey(x => x.StateCode);
        });

        // Apply all entity configurations from assembly
        builder.ApplyConfigurationsFromAssembly(typeof(ApplicationDbContext).Assembly);

        // Rename Identity tables
        builder.Entity<ApplicationUser>().ToTable("Users");
        builder.Entity<Microsoft.AspNetCore.Identity.IdentityRole>().ToTable("Roles");
        builder.Entity<Microsoft.AspNetCore.Identity.IdentityUserRole<string>>().ToTable("UserRoles");
        builder.Entity<Microsoft.AspNetCore.Identity.IdentityUserClaim<string>>().ToTable("UserClaims");
        builder.Entity<Microsoft.AspNetCore.Identity.IdentityUserLogin<string>>().ToTable("UserLogins");
        builder.Entity<Microsoft.AspNetCore.Identity.IdentityRoleClaim<string>>().ToTable("RoleClaims");
        builder.Entity<Microsoft.AspNetCore.Identity.IdentityUserToken<string>>().ToTable("UserTokens");

        // SolarRequest config
        builder.Entity<SolarRequest>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.RequestNumber).HasMaxLength(20).IsRequired();
            e.HasIndex(x => x.RequestNumber).IsUnique();
            e.Property(x => x.PlanAmount).HasColumnType("decimal(18,2)");
            e.Property(x => x.KVCapacity).HasColumnType("decimal(8,2)");
            e.HasOne(x => x.User)
             .WithMany(u => u.SolarRequests)
             .HasForeignKey(x => x.UserId)
             .OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.SolarProject)
             .WithMany(p => p.Requests)
             .HasForeignKey(x => x.SolarProjectId)
             .OnDelete(DeleteBehavior.SetNull);
            e.HasQueryFilter(x => !x.IsDeleted);
        });

        // SolarProject config
        builder.Entity<SolarProject>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(100).IsRequired();
            e.Property(x => x.SolarTypeKV).HasColumnType("decimal(8,2)");
            e.Property(x => x.DiscomWork).HasColumnType("decimal(18,2)");
            e.Property(x => x.DealClose).HasColumnType("decimal(18,2)");
            e.Property(x => x.SCZMenue).HasColumnType("decimal(18,2)");
            e.Property(x => x.SportainTeam).HasColumnType("decimal(18,2)");
            e.Property(x => x.TotalAmount).HasColumnType("decimal(18,2)");
            e.HasQueryFilter(x => !x.IsDeleted);
        });

        // Payment
        builder.Entity<Payment>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Amount).HasColumnType("decimal(18,2)");
            e.HasOne(x => x.SolarRequest)
             .WithMany(r => r.Payments)
             .HasForeignKey(x => x.SolarRequestId)
             .OnDelete(DeleteBehavior.Cascade);
            e.HasQueryFilter(x => !x.IsDeleted);
        });

        // Document
        builder.Entity<Document>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasOne(x => x.SolarRequest)
             .WithMany(r => r.Documents)
             .HasForeignKey(x => x.SolarRequestId)
             .OnDelete(DeleteBehavior.Cascade);
            e.HasQueryFilter(x => !x.IsDeleted);
        });

        // Commission
        builder.Entity<Commission>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.ProjectAmount).HasColumnType("decimal(18,2)");
            e.Property(x => x.CommissionAmount).HasColumnType("decimal(18,2)");
            e.Property(x => x.CommissionPercentage).HasColumnType("decimal(5,2)");
            e.HasOne(x => x.SolarRequest)
             .WithOne(r => r.Commission)
             .HasForeignKey<Commission>(x => x.SolarRequestId)
             .OnDelete(DeleteBehavior.Cascade);
            e.HasQueryFilter(x => !x.IsDeleted);
        });

        // WorkerAssignment
        builder.Entity<WorkerAssignment>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasOne(x => x.Worker)
             .WithMany(w => w.Assignments)
             .HasForeignKey(x => x.WorkerId)
             .OnDelete(DeleteBehavior.Restrict);
            e.HasQueryFilter(x => !x.IsDeleted);
        });

        // Apply soft delete filter to all BaseEntity types
        foreach (var entityType in builder.Model.GetEntityTypes())
        {
            if (typeof(Domain.Common.BaseEntity).IsAssignableFrom(entityType.ClrType))
            {
                // Already applied per entity above
            }
        }

        // SolarAccount config
        builder.Entity<SolarAccount>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.AccountNumber).HasMaxLength(20).IsRequired();
            e.HasIndex(x => x.AccountNumber).IsUnique();
            e.Property(x => x.ProjectAmount).HasColumnType("decimal(18,2)");
            e.Property(x => x.DepositAmount).HasColumnType("decimal(18,2)");
            e.Property(x => x.DueAmount).HasColumnType("decimal(18,2)");
            e.HasOne(x => x.User)
             .WithMany()
             .HasForeignKey(x => x.UserId)
             .OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.SolarRequest)
             .WithMany()
             .HasForeignKey(x => x.SolarRequestId)
             .OnDelete(DeleteBehavior.Cascade);
            e.HasQueryFilter(x => !x.IsDeleted);
        });

        // PMDocument config
        builder.Entity<PMDocument>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.FileName).HasMaxLength(255).IsRequired();
            e.Property(x => x.FilePath).HasMaxLength(500).IsRequired();
            e.HasOne(x => x.SolarRequest)
             .WithMany()
             .HasForeignKey(x => x.SolarRequestId)
             .OnDelete(DeleteBehavior.Cascade);
            e.HasQueryFilter(x => !x.IsDeleted);
        });

        // Wallet config
        builder.Entity<Wallet>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.TotalIncome).HasColumnType("decimal(18,2)");
            e.Property(x => x.TDS).HasColumnType("decimal(18,2)");
            e.Property(x => x.NetAmount).HasColumnType("decimal(18,2)");
            e.Property(x => x.WithdrawnAmount).HasColumnType("decimal(18,2)");
            e.Property(x => x.PendingBalance).HasColumnType("decimal(18,2)");
            e.HasOne(x => x.User)
             .WithMany()
             .HasForeignKey(x => x.UserId)
             .OnDelete(DeleteBehavior.Cascade);
            e.HasQueryFilter(x => !x.IsDeleted);
        });

        // WalletTransaction config
        builder.Entity<WalletTransaction>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Amount).HasColumnType("decimal(18,2)");
            e.HasOne(x => x.Wallet)
             .WithMany(w => w.Transactions)
             .HasForeignKey(x => x.WalletId)
             .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.SolarAccount)
             .WithMany()
             .HasForeignKey(x => x.SolarAccountId)
             .OnDelete(DeleteBehavior.SetNull);
            e.HasQueryFilter(x => !x.IsDeleted);
        });

        // Withdrawal config
        builder.Entity<Withdrawal>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Amount).HasColumnType("decimal(18,2)");
            e.HasOne(x => x.Wallet)
             .WithMany(w => w.Withdrawals)
             .HasForeignKey(x => x.WalletId)
             .OnDelete(DeleteBehavior.Cascade);
            e.HasQueryFilter(x => !x.IsDeleted);
        });

        // ActivityLog config
        builder.Entity<ActivityLog>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Action).HasMaxLength(100).IsRequired();
            e.Property(x => x.EntityName).HasMaxLength(100).IsRequired();
            e.Property(x => x.EntityId).HasMaxLength(50).IsRequired();
            e.Property(x => x.IpAddress).HasMaxLength(45).IsRequired();
            e.HasQueryFilter(x => !x.IsDeleted);
        });
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        var entries = ChangeTracker.Entries()
            .Where(e => e.Entity is Domain.Common.BaseEntity &&
                        (e.State == EntityState.Added || e.State == EntityState.Modified));

        foreach (var entry in entries)
        {
            if (entry.Entity is Domain.Common.BaseEntity entity)
            {
                if (entry.State == EntityState.Added)
                    entity.CreatedAt = DateTime.UtcNow;
                else
                    entity.UpdatedAt = DateTime.UtcNow;
            }
        }

        return base.SaveChangesAsync(cancellationToken);
    }
}