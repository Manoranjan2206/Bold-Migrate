using System.Security.Cryptography;
using BoldMigrate.Core;
using BoldMigrate.Core.Configuration;
using BoldMigrate.Core.Enums;
using BoldMigrate.Core.Interfaces;
using BoldMigrate.Core.Models;
using BoldMigrate.Core.StateMachine;
using BoldMigrate.Engine.Logging;
using BoldMigrate.Engine.Transform;
using BoldMigrate.Engine.Upload;
using BoldMigrate.Engine.Validation;
using BoldMigrate.Infrastructure.Credentials;
using BoldMigrate.Infrastructure.SourceAdapters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BoldMigrate.Engine.Orchestrator;

public class MigrationOrchestrator : BackgroundService, IMigrationOrchestrator
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IStagingStore _staging;
    private readonly ICredentialStore _credStore;
    private readonly ILogger<MigrationOrchestrator> _sysLogger;
    private readonly MigrationSettings _settings;

    private readonly SemaphoreSlim _runLock = new(1, 1);
    private volatile bool _isPaused;
    public DateTime LastHeartbeatUtc { get; private set; } = DateTime.UtcNow;

    public MigrationOrchestrator(
        IServiceScopeFactory scopeFactory,
        IStagingStore staging,
        ICredentialStore credStore,
        ILogger<MigrationOrchestrator> sysLogger,
        MigrationSettings? settings = null)
    {
        _scopeFactory = scopeFactory;
        _staging = staging;
        _credStore = credStore;
        _sysLogger = sysLogger;
        _settings = settings ?? new MigrationSettings();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _sysLogger.LogInformation("MigrationOrchestrator background service started.");

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                LastHeartbeatUtc = DateTime.UtcNow;
                await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Graceful shutdown
        }
    }

    public async Task<MigrationRun> CreateOrGetActiveRunAsync(string name, string sourceUrl, CancellationToken ct = default)
    {
        await _runLock.WaitAsync(ct);
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<IMigrationStore>();
            var logger = scope.ServiceProvider.GetRequiredService<MigrationLogger>();

            var active = await store.GetActiveRunAsync(ct);
            if (active != null)
            {
                return active;
            }

            var runId = Guid.NewGuid();
            var run = new MigrationRun
            {
                Id = runId,
                Name = name,
                Status = RunStatus.Draft,
                CurrentStep = 1,
                SourceServerUrl = sourceUrl,
                SourceVersion = "SSRS 2022",
                SourceApi = SourceApi.RestV2,
                CredentialMode = CredentialMode.InMemory,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow,
                CreatedBy = "Administrator",
                StagingPath = _staging.GetRunStagingPath(runId)
            };

            await store.CreateRunAsync(run, ct);
            logger.Log(LogLevel.Information, "RUN-START", run.Id, $"Created migration run '{name}'.", step: 1);
            return run;
        }
        finally
        {
            _runLock.Release();
        }
    }

    public async Task<MigrationRun?> GetRunAsync(Guid runId, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IMigrationStore>();
        return await store.GetRunAsync(runId, ct);
    }

    public async Task<MigrationRun?> GetActiveRunAsync(CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IMigrationStore>();
        return await store.GetActiveRunAsync(ct);
    }

    public void PauseRun(Guid runId)
    {
        _isPaused = true;
        using var scope = _scopeFactory.CreateScope();
        var logger = scope.ServiceProvider.GetRequiredService<MigrationLogger>();
        logger.Log(LogLevel.Information, "UPLD-PAUSE", runId, "Upload paused by user.");
    }

    public void ResumeRun(Guid runId)
    {
        _isPaused = false;
        using var scope = _scopeFactory.CreateScope();
        var logger = scope.ServiceProvider.GetRequiredService<MigrationLogger>();
        logger.Log(LogLevel.Information, "UPLD-RESUME", runId, "Upload resumed by user.");
    }

    public async Task StartFetchAsync(Guid runId, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IMigrationStore>();
        var logger = scope.ServiceProvider.GetRequiredService<MigrationLogger>();

        var run = await store.GetRunAsync(runId, ct) ?? throw new KeyNotFoundException("Run not found.");
        run.Status = RunStatus.Connected;
        run.CurrentStep = 2;
        await store.UpdateRunAsync(run, ct);

        logger.Log(LogLevel.Information, "DISC-START", runId, "Starting catalogue discovery from SSRS.", step: 2);

        var cred = _credStore.GetSourceCredential(runId.ToString());
        var adapter = SourceAdapterFactory.Create(run.SourceServerUrl, run.SourceApi, cred);
        var sourceItems = await adapter.ListItemsAsync(ct);

        var catalogItems = new List<CatalogItem>();
        var reportRecords = new List<ReportRecord>();
        // Re-running discovery must not add the same items again: skip anything this run already has.
        var existing = await store.GetCatalogItemsAsync(runId, null, ct);
        var known = new HashSet<string>(
            existing.Select(e => $"{(int)e.ItemType}|{e.SourcePath}"), StringComparer.OrdinalIgnoreCase);
        foreach (var s in sourceItems)
        {
            if (!known.Add($"{(int)s.ItemType}|{s.Path}")) continue;
            var cid = Guid.NewGuid();
            var item = new CatalogItem
            {
                Id = cid,
                RunId = runId,
                ItemType = s.ItemType,
                SourcePath = s.Path,
                SourceItemId = s.Id,
                Name = s.Name,
                ParentPath = s.ParentPath ?? "/",
                SizeBytes = s.SizeBytes,
                ModifiedUtc = s.ModifiedUtc
            };

            // Download definition to staging
            if (s.ItemType is ItemType.Report or ItemType.DataSet or ItemType.DataSource)
            {
                try
                {
                    var defBytes = await adapter.GetDefinitionAsync(s.Id, ct);
                    if (defBytes.Length > 0)
                    {
                        var stagedPath = await _staging.StageFileAsync(runId, s.Path, defBytes, ct);
                        item.StagedFilePath = stagedPath;
                        item.Hash = Convert.ToHexString(SHA256.HashData(defBytes)).ToLowerInvariant();
                    }
                }
                catch
                {
                    // Fallback
                }
            }

            catalogItems.Add(item);

            // Also create ReportRecord for Reports
            if (s.ItemType == ItemType.Report)
            {
                var record = new ReportRecord
                {
                    CatalogItemId = cid,
                    State = ReportState.Discovered,
                    Bucket = ReportBucket.Clean,
                    IdempotencyKey = UploadLayer.ComputeIdempotencyKey(runId, ItemType.Report, s.Path)
                };
                reportRecords.Add(record);
            }
        }

        await store.AddCatalogItemsAsync(catalogItems, ct);
        foreach (var record in reportRecords)
        {
            await store.AddReportRecordAsync(record, ct);
        }

        run.Status = RunStatus.Discovered;
        await store.UpdateRunAsync(run, ct);

        logger.Log(
            LogLevel.Information,
            "DISC-COMPLETE",
            runId,
            $"Discovery complete. Discovered {catalogItems.Count} items ({catalogItems.Count(i => i.ItemType == ItemType.Report)} reports).",
            step: 2);
    }

    public async Task StartCheckAsync(Guid runId, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IMigrationStore>();
        var logger = scope.ServiceProvider.GetRequiredService<MigrationLogger>();
        var transform = scope.ServiceProvider.GetRequiredService<TransformEngine>();

        var run = await store.GetRunAsync(runId, ct) ?? throw new KeyNotFoundException("Run not found.");
        run.CurrentStep = 3;
        await store.UpdateRunAsync(run, ct);

        logger.Log(LogLevel.Information, "XFRM-START", runId, "Starting transformation and compatibility check.", step: 3);

        var reports = await store.GetCatalogItemsAsync(runId, ItemType.Report, ct);

        foreach (var report in reports)
        {
            var record = await store.GetReportRecordAsync(report.Id, ct);
            if (record == null) continue;

            if (string.IsNullOrEmpty(report.StagedFilePath) || !File.Exists(report.StagedFilePath))
            {
                continue;
            }

            var content = await _staging.ReadStagedFileAsync(report.StagedFilePath, ct);
            var xfrm = transform.TransformAndClassify(report.Id, content);

            record.Bucket = xfrm.Bucket;
            record.BlockReasonCode = xfrm.BlockReasonCode;
            record.RdlSchemaVersion = xfrm.SchemaVersion;
            record.FeatureTags = xfrm.FeatureTags.Count > 0 ? string.Join(",", xfrm.FeatureTags) : null;
            record.State = xfrm.Bucket == ReportBucket.Blocked ? ReportState.Blocked : ReportState.Analysed;

            foreach (var act in xfrm.Actions)
            {
                await store.AddTransformActionAsync(act, ct);
            }

            await store.UpdateReportRecordAsync(record, ct);

            // Save rewritten bytes back to staging
            if (xfrm.RewrittenBytes.Length > 0)
            {
                await _staging.StageFileAsync(runId, report.SourcePath, xfrm.RewrittenBytes, ct);
            }
        }

        run.Status = RunStatus.Analysed;
        await store.UpdateRunAsync(run, ct);

        logger.Log(LogLevel.Information, "XFRM-COMPLETE", runId, "Transformation check complete.", step: 3);
    }

    public async Task StartUploadAsync(Guid runId, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IMigrationStore>();
        var logger = scope.ServiceProvider.GetRequiredService<MigrationLogger>();
        var upload = scope.ServiceProvider.GetRequiredService<UploadLayer>();

        var run = await store.GetRunAsync(runId, ct) ?? throw new KeyNotFoundException("Run not found.");
        run.Status = RunStatus.Uploading;
        run.CurrentStep = 5;
        await store.UpdateRunAsync(run, ct);

        logger.Log(LogLevel.Information, "UPLD-STAGE", runId, "Starting 4-stage upload to Bold Reports Server.", step: 5);

        var destUrl = !string.IsNullOrEmpty(run.DestinationServerUrl) ? run.DestinationServerUrl : run.SourceServerUrl;
        var token = _credStore.GetDestinationToken();
        var destClient = new DestinationClient(destUrl, token);
        await upload.UploadEstateAsync(runId, destClient, () => _isPaused, ct);

        run.Status = RunStatus.Uploaded;
        await store.UpdateRunAsync(run, ct);
    }

    public async Task StartValidationAsync(Guid runId, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IMigrationStore>();
        var logger = scope.ServiceProvider.GetRequiredService<MigrationLogger>();
        var validation = scope.ServiceProvider.GetRequiredService<ValidationEngine>();

        var run = await store.GetRunAsync(runId, ct) ?? throw new KeyNotFoundException("Run not found.");
        run.Status = RunStatus.Validating;
        run.CurrentStep = 4;
        await store.UpdateRunAsync(run, ct);

        logger.Log(LogLevel.Information, "VALD-START", runId, "Starting verification pass on staged reports and SSRS source.", step: 4);

        var cred = _credStore.GetSourceCredential(runId.ToString());
        var adapter = SourceAdapterFactory.Create(run.SourceServerUrl, run.SourceApi, cred);
        var reports = await store.GetCatalogItemsAsync(runId, ItemType.Report, ct);

        var destToken = _credStore.GetDestinationToken();
        DestinationClient? destClient = null;
        if (!string.IsNullOrEmpty(run.DestinationServerUrl) && !string.IsNullOrEmpty(destToken))
        {
            destClient = new DestinationClient(run.DestinationServerUrl, destToken);
        }

        var stagedRenderer = scope.ServiceProvider.GetService<IStagedReportRenderer>();

        foreach (var r in reports)
        {
            var record = await store.GetReportRecordAsync(r.Id, ct);
            if (record == null || record.State == ReportState.Blocked || record.State == ReportState.Excluded) continue;

            await validation.ValidateReportAsync(runId, r, record, adapter, destClient, stagedRenderer, ct);
        }

        run.Status = RunStatus.Validated;
        await store.UpdateRunAsync(run, ct);
    }

    public async Task RetryFailedItemsAsync(Guid runId, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IMigrationStore>();

        var failedReports = await store.GetReportRecordsAsync(runId, ReportState.UploadFailed, ct: ct);
        foreach (var r in failedReports)
        {
            r.State = ReportState.Queued;
            await store.UpdateReportRecordAsync(r, ct);
        }

        await StartUploadAsync(runId, ct);
    }

    public async Task CompleteCutoverAsync(Guid runId, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IMigrationStore>();
        var logger = scope.ServiceProvider.GetRequiredService<MigrationLogger>();

        var run = await store.GetRunAsync(runId, ct) ?? throw new KeyNotFoundException("Run not found.");
        run.Status = RunStatus.CutOver;
        run.CurrentStep = 6;
        await store.UpdateRunAsync(run, ct);

        if (_settings.StagingDeleteOnClose)
        {
            await _staging.DeleteRunStagingAsync(runId, ct);
        }

        _credStore.ClearAll();
        logger.Log(LogLevel.Information, "RUN-COMPLETE", runId, "SSRS cutover confirmed. Migration complete.", step: 6);
    }

    public async Task AbandonRunAsync(Guid runId, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IMigrationStore>();
        var logger = scope.ServiceProvider.GetRequiredService<MigrationLogger>();

        var run = await store.GetRunAsync(runId, ct);
        if (run != null)
        {
            run.Status = RunStatus.Abandoned;
            await store.UpdateRunAsync(run, ct);
            await _staging.DeleteRunStagingAsync(runId, ct);
            _credStore.ClearAll();
            logger.Log(LogLevel.Warning, "RUN-ABANDON", runId, "Migration run abandoned by user.");
        }
    }
}
