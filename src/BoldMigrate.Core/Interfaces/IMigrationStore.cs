using BoldMigrate.Core.Enums;
using BoldMigrate.Core.Models;

namespace BoldMigrate.Core.Interfaces;

/// <summary>
/// Repository interface for persisting and querying all migration run state, items, decisions, and audit events.
/// </summary>
public interface IMigrationStore
{
    // --- MigrationRun ---
    Task<MigrationRun?> GetRunAsync(Guid runId, CancellationToken ct = default);
    Task<MigrationRun?> GetActiveRunAsync(CancellationToken ct = default);
    Task<IReadOnlyList<MigrationRun>> ListRunsAsync(CancellationToken ct = default);
    Task CreateRunAsync(MigrationRun run, CancellationToken ct = default);
    Task UpdateRunAsync(MigrationRun run, CancellationToken ct = default);
    Task DeleteRunAsync(Guid runId, CancellationToken ct = default);

    // --- CatalogItem ---
    Task<CatalogItem?> GetCatalogItemAsync(Guid id, CancellationToken ct = default);
    Task<CatalogItem?> GetCatalogItemByPathAsync(Guid runId, string sourcePath, CancellationToken ct = default);
    Task<IReadOnlyList<CatalogItem>> GetCatalogItemsAsync(Guid runId, ItemType? itemType = null, CancellationToken ct = default);
    Task AddCatalogItemsAsync(IEnumerable<CatalogItem> items, CancellationToken ct = default);
    Task UpdateCatalogItemAsync(CatalogItem item, CancellationToken ct = default);
    Task DeleteCatalogItemAsync(Guid id, CancellationToken ct = default);

    // --- ReportRecord ---
    Task<ReportRecord?> GetReportRecordAsync(Guid catalogItemId, CancellationToken ct = default);
    Task<IReadOnlyList<ReportRecord>> GetReportRecordsAsync(Guid runId, ReportState? state = null, ReportBucket? bucket = null, CancellationToken ct = default);
    Task AddReportRecordAsync(ReportRecord record, CancellationToken ct = default);
    Task UpdateReportRecordAsync(ReportRecord record, CancellationToken ct = default);
    Task DeleteReportRecordAsync(Guid catalogItemId, CancellationToken ct = default);

    // --- Supporting Entities ---
    Task AddTransformActionAsync(TransformAction action, CancellationToken ct = default);
    Task<IReadOnlyList<TransformAction>> GetTransformActionsAsync(Guid reportId, CancellationToken ct = default);

    Task AddUploadAttemptAsync(UploadAttempt attempt, CancellationToken ct = default);
    Task<IReadOnlyList<UploadAttempt>> GetUploadAttemptsAsync(Guid reportId, CancellationToken ct = default);

    Task AddValidationResultAsync(ValidationResult result, CancellationToken ct = default);
    Task<IReadOnlyList<ValidationResult>> GetValidationResultsAsync(Guid reportId, CancellationToken ct = default);

    Task AddApprovalAsync(Approval approval, CancellationToken ct = default);
    Task<Approval?> GetApprovalAsync(Guid reportId, CancellationToken ct = default);

    Task AddAuditEventAsync(AuditEvent auditEvent, CancellationToken ct = default);
    Task<IReadOnlyList<AuditEvent>> GetAuditEventsAsync(Guid runId, CancellationToken ct = default);

    Task AddPermissionMappingsAsync(IEnumerable<PermissionMapping> mappings, CancellationToken ct = default);
    Task<IReadOnlyList<PermissionMapping>> GetPermissionMappingsAsync(Guid runId, CancellationToken ct = default);

    Task AddSubscriptionRecordsAsync(IEnumerable<SubscriptionRecord> subscriptions, CancellationToken ct = default);
    Task<IReadOnlyList<SubscriptionRecord>> GetSubscriptionRecordsAsync(Guid runId, CancellationToken ct = default);

    Task AddUrlMappingsAsync(IEnumerable<UrlMapping> mappings, CancellationToken ct = default);
    Task<IReadOnlyList<UrlMapping>> GetUrlMappingsAsync(Guid runId, CancellationToken ct = default);
}
