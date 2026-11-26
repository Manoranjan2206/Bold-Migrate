using System.Security.Cryptography;
using BoldMigrate.Core.Configuration;
using BoldMigrate.Core.Interfaces;
using BoldMigrate.Core.Models;
using BoldMigrate.Engine.Logging;
using BoldMigrate.Engine.Preview;
using BoldMigrate.Infrastructure.Credentials;

namespace BoldMigrate.Api.Preview;

/// <summary>
/// Builds the per-request <see cref="StagedReportServer"/> for a run and persists reports
/// saved from the local designer back into staging.
/// </summary>
public sealed class StagedReportSession
{
    private readonly IMigrationStore _store;
    private readonly IStagingStore _staging;
    private readonly ICredentialStore _credentials;
    private readonly MigrationSettings _settings;
    private readonly MigrationLogger _migrationLogger;
    private readonly ILogger<StagedReportSession> _logger;

    public StagedReportSession(
        IMigrationStore store,
        IStagingStore staging,
        ICredentialStore credentials,
        MigrationSettings settings,
        MigrationLogger migrationLogger,
        ILogger<StagedReportSession> logger)
    {
        _store = store;
        _staging = staging;
        _credentials = credentials;
        _settings = settings;
        _migrationLogger = migrationLogger;
        _logger = logger;
    }

    public async Task<StagedReportServer?> CreateServerAsync(Guid runId, CancellationToken ct = default)
    {
        if (await _store.GetRunAsync(runId, ct) == null) return null;

        var catalog = new StagedCatalog(runId, await _store.GetCatalogItemsAsync(runId, ct: ct));
        // The Bold Reports helpers call the server synchronously, so the save runs synchronously too.
        return new StagedReportServer(
            catalog,
            _credentials,
            (item, bytes) => SaveReportAsync(item, bytes).GetAwaiter().GetResult(),
            _logger,
            _settings.PreviewAllowUntrustedSqlCert);
    }

    /// <summary>
    /// Writes a designer save over the staged report and marks it for upload. The edit is only
    /// sent to Bold Reports Server by the upload step; nothing is fetched from SSRS again.
    /// </summary>
    public async Task<string?> SaveReportAsync(CatalogItem item, byte[] content, CancellationToken ct = default)
    {
        if (!ReportDependencies.IsReportDefinition(content))
        {
            return "The designer sent content that is not a valid report definition, so nothing was saved.";
        }

        var record = await _store.GetReportRecordAsync(item.Id, ct);
        if (record == null)
        {
            return "This report is not tracked by the migration run, so it cannot be saved.";
        }

        var stagedPath = await _staging.StageFileAsync(item.RunId, item.SourcePath, content, ct);

        // Re-read the tracked entity so the update does not collide with the record above.
        var tracked = await _store.GetCatalogItemAsync(item.Id, ct) ?? item;
        tracked.StagedFilePath = stagedPath;
        tracked.Hash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        await _store.UpdateCatalogItemAsync(tracked, ct);

        record.LocalEditUtc = DateTime.UtcNow;
        record.LocalEditPendingUpload = true;
        await _store.UpdateReportRecordAsync(record, ct);

        _migrationLogger.Log(
            LogLevel.Information,
            "PREV-SAVE",
            item.RunId,
            $"Report '{item.Name}' was edited in the local designer and saved to staging. "
            + "It will be sent to Bold Reports Server by the next upload.",
            itemPath: item.SourcePath,
            outcome: "LocalEdit");

        return null;
    }
}
