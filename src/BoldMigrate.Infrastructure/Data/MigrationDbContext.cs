using BoldMigrate.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace BoldMigrate.Infrastructure.Data;

public class MigrationDbContext : DbContext
{
    public MigrationDbContext(DbContextOptions<MigrationDbContext> options) : base(options) { }

    public DbSet<MigrationRun> MigrationRuns => Set<MigrationRun>();
    public DbSet<CatalogItem> CatalogItems => Set<CatalogItem>();
    public DbSet<ReportRecord> ReportRecords => Set<ReportRecord>();
    public DbSet<TransformAction> TransformActions => Set<TransformAction>();
    public DbSet<UploadAttempt> UploadAttempts => Set<UploadAttempt>();
    public DbSet<ValidationResult> ValidationResults => Set<ValidationResult>();
    public DbSet<Approval> Approvals => Set<Approval>();
    public DbSet<PermissionMapping> PermissionMappings => Set<PermissionMapping>();
    public DbSet<SubscriptionRecord> SubscriptionRecords => Set<SubscriptionRecord>();
    public DbSet<UrlMapping> UrlMappings => Set<UrlMapping>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<MigrationRun>(b =>
        {
            b.HasKey(r => r.Id);
            b.Property(r => r.Status).HasConversion<string>();
            b.Property(r => r.SourceApi).HasConversion<string>();
            b.Property(r => r.CredentialMode).HasConversion<string>();
            b.HasMany(r => r.CatalogItems)
             .WithOne(c => c.MigrationRun)
             .HasForeignKey(c => c.RunId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CatalogItem>(b =>
        {
            b.HasKey(c => c.Id);
            b.Property(c => c.ItemType).HasConversion<string>();
            b.HasIndex(c => new { c.RunId, c.SourcePath });
            b.HasOne(c => c.ReportRecord)
             .WithOne(r => r.CatalogItem)
             .HasForeignKey<ReportRecord>(r => r.CatalogItemId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ReportRecord>(b =>
        {
            b.HasKey(r => r.CatalogItemId);
            b.Property(r => r.Bucket).HasConversion<string>();
            b.Property(r => r.State).HasConversion<string>();
            b.HasIndex(r => r.State);
            b.HasIndex(r => r.Bucket);
            b.HasIndex(r => r.IdempotencyKey);
        });

        modelBuilder.Entity<TransformAction>(b =>
        {
            b.HasKey(t => t.Id);
            b.HasIndex(t => t.ReportId);
        });

        modelBuilder.Entity<UploadAttempt>(b =>
        {
            b.HasKey(u => u.Id);
            b.HasIndex(u => u.ReportId);
        });

        modelBuilder.Entity<ValidationResult>(b =>
        {
            b.HasKey(v => v.Id);
            b.HasIndex(v => v.ReportId);
        });

        modelBuilder.Entity<Approval>(b =>
        {
            b.HasKey(a => a.Id);
            b.HasIndex(a => a.ReportId);
        });

        modelBuilder.Entity<PermissionMapping>(b =>
        {
            b.HasKey(p => p.Id);
            b.HasIndex(p => p.RunId);
        });

        modelBuilder.Entity<SubscriptionRecord>(b =>
        {
            b.HasKey(s => s.Id);
            b.HasIndex(s => s.RunId);
        });

        modelBuilder.Entity<UrlMapping>(b =>
        {
            b.HasKey(u => u.Id);
            b.HasIndex(u => new { u.RunId, u.SourcePath });
        });

        modelBuilder.Entity<AuditEvent>(b =>
        {
            b.HasKey(a => a.Id);
            b.HasIndex(a => a.RunId);
        });
    }
}
