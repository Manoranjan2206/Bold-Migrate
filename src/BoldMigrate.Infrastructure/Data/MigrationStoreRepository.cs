using BoldMigrate.Core.Enums;
using BoldMigrate.Core.Interfaces;
using BoldMigrate.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace BoldMigrate.Infrastructure.Data;

public class MigrationStoreRepository : IMigrationStore
{
    private readonly MigrationDbContext _db;

    public MigrationStoreRepository(MigrationDbContext db)
    {
        _db = db;
    }

    public async Task<MigrationRun?> GetRunAsync(Guid runId, CancellationToken ct = default)
    {
        return await _db.MigrationRuns
            .Include(r => r.CatalogItems)
            .FirstOrDefaultAsync(r => r.Id == runId, ct);
    }

    public async Task<MigrationRun?> GetActiveRunAsync(CancellationToken ct = default)
    {
        return await _db.MigrationRuns
            .Include(r => r.CatalogItems)
            .FirstOrDefaultAsync(r => r.Status != RunStatus.CutOver && r.Status != RunStatus.Abandoned, ct);
    }

    public async Task<IReadOnlyList<MigrationRun>> ListRunsAsync(CancellationToken ct = default)
    {
        return await _db.MigrationRuns.OrderByDescending(r => r.CreatedUtc).ToListAsync(ct);
    }

    public async Task CreateRunAsync(MigrationRun run, CancellationToken ct = default)
    {
        _db.MigrationRuns.Add(run);
        await _db.SaveChangesAsync(ct);
    }

    public async Task UpdateRunAsync(MigrationRun run, CancellationToken ct = default)
    {
        run.UpdatedUtc = DateTime.UtcNow;
        _db.MigrationRuns.Update(run);
        await _db.SaveChangesAsync(ct);
    }

    public async Task DeleteRunAsync(Guid runId, CancellationToken ct = default)
    {
        var run = await _db.MigrationRuns.FindAsync(new object[] { runId }, ct);
        if (run != null)
        {
            _db.MigrationRuns.Remove(run);
            await _db.SaveChangesAsync(ct);
        }
    }

    public async Task<CatalogItem?> GetCatalogItemAsync(Guid id, CancellationToken ct = default)
    {
        return await _db.CatalogItems
            .Include(c => c.ReportRecord)
            .FirstOrDefaultAsync(c => c.Id == id, ct);
    }

    public async Task<CatalogItem?> GetCatalogItemByPathAsync(Guid runId, string sourcePath, CancellationToken ct = default)
    {
        return await _db.CatalogItems
            .Include(c => c.ReportRecord)
            .FirstOrDefaultAsync(c => c.RunId == runId && c.SourcePath == sourcePath, ct);
    }

    public async Task<IReadOnlyList<CatalogItem>> GetCatalogItemsAsync(Guid runId, ItemType? itemType = null, CancellationToken ct = default)
    {
        var query = _db.CatalogItems
            .Include(c => c.ReportRecord)
            .Where(c => c.RunId == runId);

        if (itemType.HasValue)
        {
            query = query.Where(c => c.ItemType == itemType.Value);
        }

        return await query.ToListAsync(ct);
    }

    public async Task AddCatalogItemsAsync(IEnumerable<CatalogItem> items, CancellationToken ct = default)
    {
        await _db.CatalogItems.AddRangeAsync(items, ct);
        await _db.SaveChangesAsync(ct);
    }

    public async Task UpdateCatalogItemAsync(CatalogItem item, CancellationToken ct = default)
    {
        _db.CatalogItems.Update(item);
        await _db.SaveChangesAsync(ct);
    }

    public async Task DeleteCatalogItemAsync(Guid id, CancellationToken ct = default)
    {
        var item = await _db.CatalogItems.FindAsync(new object[] { id }, ct);
        if (item != null)
        {
            _db.CatalogItems.Remove(item);
            await _db.SaveChangesAsync(ct);
        }
    }

    public async Task<ReportRecord?> GetReportRecordAsync(Guid catalogItemId, CancellationToken ct = default)
    {
        return await _db.ReportRecords
            .Include(r => r.CatalogItem)
            .FirstOrDefaultAsync(r => r.CatalogItemId == catalogItemId, ct);
    }

    public async Task<IReadOnlyList<ReportRecord>> GetReportRecordsAsync(Guid runId, ReportState? state = null, ReportBucket? bucket = null, CancellationToken ct = default)
    {
        var query = _db.ReportRecords
            .Include(r => r.CatalogItem)
            .Where(r => r.CatalogItem != null && r.CatalogItem.RunId == runId);

        if (state.HasValue) query = query.Where(r => r.State == state.Value);
        if (bucket.HasValue) query = query.Where(r => r.Bucket == bucket.Value);

        return await query.ToListAsync(ct);
    }

    public async Task AddReportRecordAsync(ReportRecord record, CancellationToken ct = default)
    {
        _db.ReportRecords.Add(record);
        await _db.SaveChangesAsync(ct);
    }

    public async Task UpdateReportRecordAsync(ReportRecord record, CancellationToken ct = default)
    {
        _db.ReportRecords.Update(record);
        await _db.SaveChangesAsync(ct);
    }

    public async Task DeleteReportRecordAsync(Guid catalogItemId, CancellationToken ct = default)
    {
        var record = await _db.ReportRecords.FindAsync(new object[] { catalogItemId }, ct);
        if (record != null)
        {
            _db.ReportRecords.Remove(record);
            await _db.SaveChangesAsync(ct);
        }
    }

    public async Task AddTransformActionAsync(TransformAction action, CancellationToken ct = default)
    {
        _db.TransformActions.Add(action);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<TransformAction>> GetTransformActionsAsync(Guid reportId, CancellationToken ct = default)
    {
        return await _db.TransformActions
            .Where(t => t.ReportId == reportId)
            .OrderBy(t => t.AttemptNumber)
            .ToListAsync(ct);
    }

    public async Task AddUploadAttemptAsync(UploadAttempt attempt, CancellationToken ct = default)
    {
        _db.UploadAttempts.Add(attempt);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<UploadAttempt>> GetUploadAttemptsAsync(Guid reportId, CancellationToken ct = default)
    {
        return await _db.UploadAttempts
            .Where(u => u.ReportId == reportId)
            .OrderBy(u => u.AttemptNumber)
            .ToListAsync(ct);
    }

    public async Task AddValidationResultAsync(ValidationResult result, CancellationToken ct = default)
    {
        _db.ValidationResults.Add(result);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<ValidationResult>> GetValidationResultsAsync(Guid reportId, CancellationToken ct = default)
    {
        return await _db.ValidationResults
            .Where(v => v.ReportId == reportId)
            .OrderByDescending(v => v.RunUtc)
            .ToListAsync(ct);
    }

    public async Task AddApprovalAsync(Approval approval, CancellationToken ct = default)
    {
        _db.Approvals.Add(approval);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<Approval?> GetApprovalAsync(Guid reportId, CancellationToken ct = default)
    {
        return await _db.Approvals.FirstOrDefaultAsync(a => a.ReportId == reportId, ct);
    }

    public async Task AddAuditEventAsync(AuditEvent auditEvent, CancellationToken ct = default)
    {
        _db.AuditEvents.Add(auditEvent);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<AuditEvent>> GetAuditEventsAsync(Guid runId, CancellationToken ct = default)
    {
        return await _db.AuditEvents
            .Where(a => a.RunId == runId)
            .OrderBy(a => a.OccurredUtc)
            .ToListAsync(ct);
    }

    public async Task AddPermissionMappingsAsync(IEnumerable<PermissionMapping> mappings, CancellationToken ct = default)
    {
        await _db.PermissionMappings.AddRangeAsync(mappings, ct);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<PermissionMapping>> GetPermissionMappingsAsync(Guid runId, CancellationToken ct = default)
    {
        return await _db.PermissionMappings.Where(p => p.RunId == runId).ToListAsync(ct);
    }

    public async Task AddSubscriptionRecordsAsync(IEnumerable<SubscriptionRecord> subscriptions, CancellationToken ct = default)
    {
        await _db.SubscriptionRecords.AddRangeAsync(subscriptions, ct);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<SubscriptionRecord>> GetSubscriptionRecordsAsync(Guid runId, CancellationToken ct = default)
    {
        return await _db.SubscriptionRecords.Where(s => s.RunId == runId).ToListAsync(ct);
    }

    public async Task AddUrlMappingsAsync(IEnumerable<UrlMapping> mappings, CancellationToken ct = default)
    {
        await _db.UrlMappings.AddRangeAsync(mappings, ct);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<UrlMapping>> GetUrlMappingsAsync(Guid runId, CancellationToken ct = default)
    {
        return await _db.UrlMappings.Where(u => u.RunId == runId).ToListAsync(ct);
    }
}
