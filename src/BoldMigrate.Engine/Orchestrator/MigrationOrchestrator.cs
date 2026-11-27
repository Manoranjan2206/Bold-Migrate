using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using BoldMigrate.Core;
using BoldMigrate.Core.Configuration;
using BoldMigrate.Core.Enums;
using BoldMigrate.Core.Interfaces;
using BoldMigrate.Core.Models;
using BoldMigrate.Core.StateMachine;
using BoldMigrate.Engine.Logging;
using BoldMigrate.Engine.Metrics;
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
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _runExecutionLocks = new();
    private readonly ConcurrentDictionary<Guid, bool> _pausedRuns = new();
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

    private SemaphoreSlim GetExecutionLock(Guid runId) =>
        _runExecutionLocks.GetOrAdd(runId, _ => new SemaphoreSlim(1, 1));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _sysLogger.LogInformation("MigrationOrchestrator background service started.");

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<IMigrationStore>();
            var logger = scope.ServiceProvider.GetRequiredService<MigrationLogger>();
            var active = await store.GetActiveRunAsync(stoppingToken);

            // Re-hydrate the destination origin so the CSP keeps allowing the embedded Bold
            // Reports widgets after a restart, before the operator reconnects.
            if (active != null && !string.IsNullOrWhiteSpace(active.DestinationServerUrl))
            {
                _settings.DestinationServerUrl = active.DestinationServerUrl;
            }

            if (active != null && active.Status is RunStatus.Uploading or RunStatus.Validating)
            {
                active.Status = active.Status == RunStatus.Uploading ? RunStatus.Validated : RunStatus.Analysed;
                await store.UpdateRunAsync(active, stoppingToken);
                logger.Log(LogLevel.Warning, EventCodes.RunResume, active.Id,
                    "Recovered active run from an unexpected shutdown and reset its transient stage state.");

                // Report records left mid-flight are stranded: IsEligibleForUpload excludes
                // Uploading, so without this they are skipped forever with a misleading
                // "must be validated" message. We cannot know whether the destination accepted
                // them, so mark them failed and let the existing Retry flow re-queue them.
                var stranded = await store.GetReportRecordsAsync(active.Id, ReportState.Uploading, ct: stoppingToken);
                foreach (var record in stranded)
                {
                    ReportStateMachine.Transition(record, ReportState.UploadFailed);
                    await store.UpdateReportRecordAsync(record, stoppingToken);
                }

                if (stranded.Count > 0)
                {
                    logger.Log(LogLevel.Warning, EventCodes.RunResume, active.Id,
                        $"Reset {stranded.Count} report(s) stranded in Uploading to UploadFailed so they can be retried.");
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _sysLogger.LogWarning(ex, "Could not inspect active runs during startup recovery.");
        }

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
                CredentialMode = _credStore.ActiveMode,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow,
                CreatedBy = "Administrator",
                StagingPath = _staging.GetRunStagingPath(runId)
            };

            await store.CreateRunAsync(run, ct);
            MigrationMetrics.RunsTotal.Add(1, new KeyValuePair<string, object?>("event", "start"));
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
        _pausedRuns[runId] = true;
        using var scope = _scopeFactory.CreateScope();
        var logger = scope.ServiceProvider.GetRequiredService<MigrationLogger>();
        logger.Log(LogLevel.Information, "UPLD-PAUSE", runId, "Upload paused by user.");
    }

    public void ResumeRun(Guid runId)
    {
        _pausedRuns[runId] = false;
        using var scope = _scopeFactory.CreateScope();
        var logger = scope.ServiceProvider.GetRequiredService<MigrationLogger>();
        logger.Log(LogLevel.Information, "UPLD-RESUME", runId, "Upload resumed by user.");
    }

    public async Task StartFetchAsync(Guid runId, CancellationToken ct = default)
    {
        var execLock = GetExecutionLock(runId);
        if (!await execLock.WaitAsync(0, ct))
        {
            return;
        }

        try
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
            using var adapterScope = adapter as IDisposable;
            var sourceItems = await adapter.ListItemsAsync(ct);

            var existing = await store.GetCatalogItemsAsync(runId, null, ct);
            var known = new HashSet<string>(
                existing.Select(e => $"{(int)e.ItemType}|{e.SourcePath}"), StringComparer.OrdinalIgnoreCase);

            var toAdd = sourceItems
                .Where(s => known.Add($"{(int)s.ItemType}|{s.Path}"))
                .ToList();

            var catalogBag = new ConcurrentBag<CatalogItem>();
            var recordBag = new ConcurrentBag<ReportRecord>();
            var permBag = new ConcurrentBag<PermissionMapping>();
            var subBag = new ConcurrentBag<SubscriptionRecord>();
            // Per-item discovery failures that must not be mistaken for "this item has none".
            var discoveryWarnings = new ConcurrentBag<(string Path, string Kind, string Error)>();
            var maxParallel = Math.Clamp(_settings.ExtractionMaxParallel, 1, 16);

            await Parallel.ForEachAsync(toAdd, new ParallelOptions { MaxDegreeOfParallelism = maxParallel, CancellationToken = ct }, async (s, token) =>
            {
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

                if (s.ItemType is ItemType.Report or ItemType.DataSet or ItemType.DataSource)
                {
                    try
                    {
                        var defBytes = await adapter.GetDefinitionAsync(s.Id, token);
                        if (defBytes.Length > 0)
                        {
                            if (s.ItemType == ItemType.Report)
                            {
                                await _staging.StageOriginalAsync(runId, s.Path, defBytes, token);
                            }
                            var stagedPath = await _staging.StageFileAsync(runId, s.Path, defBytes, token);
                            item.StagedFilePath = stagedPath;
                            item.Hash = Convert.ToHexString(SHA256.HashData(defBytes)).ToLowerInvariant();
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        logger.Log(LogLevel.Warning, EventCodes.DiscSkip, runId,
                            $"Could not download definition for '{s.Path}': {ex.Message}", itemPath: s.Path);
                    }
                }

                catalogBag.Add(item);

                if (s.ItemType == ItemType.Report)
                {
                    var paramCount = 0;
                    try
                    {
                        var paramsList = await adapter.GetParametersAsync(s.Id, token);
                        paramCount = paramsList.Count;
                    }
                    catch (Exception ex)
                    {
                        // Silently recording 0 parameters hid 403s and timeouts, and a report with
                        // prompt parameters then looked like one that had none.
                        discoveryWarnings.Add((s.Path, "parameters", ex.Message));
                    }

                    recordBag.Add(new ReportRecord
                    {
                        CatalogItemId = cid,
                        State = ReportState.Discovered,
                        Bucket = ReportBucket.Clean,
                        ParameterCount = paramCount,
                        IdempotencyKey = UploadLayer.ComputeIdempotencyKey(runId, ItemType.Report, s.Path)
                    });

                    try
                    {
                        var policies = await adapter.GetPoliciesAsync(s.Id, token);
                        foreach (var pol in policies)
                        {
                            permBag.Add(new PermissionMapping
                            {
                                Id = Guid.NewGuid(),
                                RunId = runId,
                                SourcePath = s.Path,
                                Principal = pol.Principal,
                                SourceRole = string.Join(", ", pol.Roles),
                                DestinationRole = string.Join(", ", pol.Roles),
                                MappingStatus = "Discovered"
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        // "No permissions on this item" and "could not read permissions" are
                        // different facts; collapsing them misrepresents the estate's security.
                        discoveryWarnings.Add((s.Path, "permissions", ex.Message));
                    }

                    try
                    {
                        var subs = await adapter.ListSubscriptionsAsync(s.Path, token);
                        foreach (var sub in subs)
                        {
                            subBag.Add(new SubscriptionRecord
                            {
                                Id = Guid.NewGuid(),
                                RunId = runId,
                                ReportPath = s.Path,
                                SubscriptionType = "Standard",
                                DeliveryMethod = sub.DeliveryMethod,
                                Schedule = sub.Schedule,
                                RecipientCount = 0,
                                MigrationPlan = "Review"
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        // An unreadable subscription list must not look like an empty one: the
                        // operator would migrate believing no schedules needed recreating.
                        discoveryWarnings.Add((s.Path, "subscriptions", ex.Message));
                    }
                }
            });

            var catalogItems = catalogBag.ToList();
            await store.AddCatalogItemsAsync(catalogItems, ct);
            foreach (var record in recordBag)
            {
                await store.AddReportRecordAsync(record, ct);
            }
            if (!permBag.IsEmpty) await store.AddPermissionMappingsAsync(permBag, ct);
            if (!subBag.IsEmpty) await store.AddSubscriptionRecordsAsync(subBag, ct);

            run.Status = RunStatus.Discovered;
            await store.UpdateRunAsync(run, ct);

            logger.Log(
                LogLevel.Information,
                "DISC-COMPLETE",
                runId,
                $"Discovery complete. Discovered {catalogItems.Count} items ({catalogItems.Count(i => i.ItemType == ItemType.Report)} reports).",
                step: 2);

            // Surface what could NOT be read. These used to be swallowed, so a 403 or a timeout
            // was indistinguishable from "this report genuinely has no parameters/permissions/
            // subscriptions" — the wrong failure mode for a migration tool.
            if (!discoveryWarnings.IsEmpty)
            {
                foreach (var group in discoveryWarnings.GroupBy(w => w.Kind))
                {
                    var affected = group.Select(w => w.Path).Distinct().ToList();
                    var sample = string.Join(", ", affected.Take(5));
                    var more = affected.Count > 5 ? $" (+{affected.Count - 5} more)" : string.Empty;

                    logger.Log(
                        LogLevel.Warning,
                        "DISC-PARTIAL",
                        runId,
                        $"Could not read {group.Key} for {affected.Count} item(s); they are recorded as empty and may be incomplete. " +
                        $"Affected: {sample}{more}. First error: {Redactor.RedactSecrets(group.First().Error)}",
                        step: 2);
                }
            }
        }
        finally
        {
            execLock.Release();
        }
    }

    public async Task StartCheckAsync(Guid runId, CancellationToken ct = default)
    {
        var execLock = GetExecutionLock(runId);
        if (!await execLock.WaitAsync(0, ct))
        {
            return;
        }

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<IMigrationStore>();
            var logger = scope.ServiceProvider.GetRequiredService<MigrationLogger>();
            var transform = scope.ServiceProvider.GetRequiredService<TransformEngine>();

            var run = await store.GetRunAsync(runId, ct) ?? throw new KeyNotFoundException("Run not found.");
            run.CurrentStep = 3;
            await store.UpdateRunAsync(run, ct);

            logger.Log(LogLevel.Information, "XFRM-START", runId, "Starting transformation and compatibility check.", step: 3);

            var allItems = await store.GetCatalogItemsAsync(runId, null, ct);
            var dsMap = allItems
                .Where(i => i.ItemType == ItemType.DataSource)
                .ToDictionary(i => i.SourcePath, i => i.Name, StringComparer.OrdinalIgnoreCase);
            var dsetMap = allItems
                .Where(i => i.ItemType == ItemType.DataSet)
                .ToDictionary(i => i.SourcePath, i => i.Name, StringComparer.OrdinalIgnoreCase);

            var reports = allItems.Where(i => i.ItemType == ItemType.Report).ToList();

            foreach (var report in reports)
            {
                var record = await store.GetReportRecordAsync(report.Id, ct);
                if (record == null) continue;

                // Read unmodified original if available, so re-running check is idempotent
                var origPath = _staging.ResolveOriginalPath(runId, report.SourcePath);
                var readPath = origPath ?? report.StagedFilePath;
                if (string.IsNullOrEmpty(readPath) || !File.Exists(readPath))
                {
                    continue;
                }

                var content = await _staging.ReadStagedFileAsync(readPath, ct);
                if (origPath == null)
                {
                    await _staging.StageOriginalAsync(runId, report.SourcePath, content, ct);
                }

                var xfrm = transform.TransformAndClassify(report.Id, content, dsMap, dsetMap);

                record.Bucket = xfrm.Bucket;
                record.BlockReasonCode = xfrm.BlockReasonCode;
                record.RdlSchemaVersion = xfrm.SchemaVersion;
                record.FeatureTags = xfrm.FeatureTags.Count > 0 ? string.Join(",", xfrm.FeatureTags) : null;
                record.ParameterCount = xfrm.ParameterCount > 0 ? xfrm.ParameterCount : record.ParameterCount;
                record.DataSourceRefs = xfrm.DataSourceNames.Count > 0 ? JsonSerializer.Serialize(xfrm.DataSourceNames) : null;
                record.DataSetRefs = xfrm.SharedDataSetReferences.Count > 0 ? JsonSerializer.Serialize(xfrm.SharedDataSetReferences) : null;
                record.State = xfrm.Bucket == ReportBucket.Blocked ? ReportState.Blocked : ReportState.Analysed;

                MigrationMetrics.ClassificationTotal.Add(1, new KeyValuePair<string, object?>("bucket", xfrm.Bucket.ToString()));

                foreach (var act in xfrm.Actions)
                {
                    await store.AddTransformActionAsync(act, ct);
                }

                await store.UpdateReportRecordAsync(record, ct);

                if (xfrm.RewrittenBytes.Length > 0)
                {
                    await _staging.StageFileAsync(runId, report.SourcePath, xfrm.RewrittenBytes, ct);
                }
            }

            run.Status = RunStatus.Analysed;
            await store.UpdateRunAsync(run, ct);

            logger.Log(LogLevel.Information, "XFRM-COMPLETE", runId, "Transformation check complete.", step: 3);
        }
        finally
        {
            execLock.Release();
        }
    }

    public Task StartUploadAsync(Guid runId, CancellationToken ct = default) =>
        StartUploadAsync(runId, conflictPolicy: null, ct);

    public async Task StartUploadAsync(Guid runId, string? conflictPolicy, CancellationToken ct = default)
    {
        var execLock = GetExecutionLock(runId);
        if (!await execLock.WaitAsync(0, ct))
        {
            return;
        }

        try
        {
            _pausedRuns[runId] = false;
            using var scope = _scopeFactory.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<IMigrationStore>();
            var logger = scope.ServiceProvider.GetRequiredService<MigrationLogger>();
            var upload = scope.ServiceProvider.GetRequiredService<UploadLayer>();

            var run = await store.GetRunAsync(runId, ct) ?? throw new KeyNotFoundException("Run not found.");
            var previousStatus = run.Status;
            run.Status = RunStatus.Uploading;
            run.CurrentStep = 5;
            await store.UpdateRunAsync(run, ct);

            var policy = Upload.UploadLayer.ParseConflictPolicy(conflictPolicy ?? _settings.UploadConflictPolicy);
            logger.Log(LogLevel.Information, "UPLD-STAGE", runId, $"Starting 4-stage upload to Bold Reports Server (conflict policy: {policy}).", step: 5);

            var destUrl = !string.IsNullOrEmpty(run.DestinationServerUrl) ? run.DestinationServerUrl : run.SourceServerUrl;
            var token = _credStore.GetDestinationToken();
            using var destClient = new DestinationClient(destUrl, token);

            try
            {
                var result = await upload.UploadEstateAsync(runId, destClient, () => _pausedRuns.TryGetValue(runId, out var p) && p, policy, ct);

                // A paused run is NOT a completed run: leaving it in Uploading keeps the cutover
                // gates honest and lets ResumeRun pick the remaining items back up.
                if (result.Paused)
                {
                    run.Status = RunStatus.Uploading;
                    await store.UpdateRunAsync(run, ct);
                    logger.Log(LogLevel.Warning, "UPLD-PAUSED", runId,
                        $"Upload paused by operator after {result.UploadedCount} item(s). Run remains in Uploading until resumed.", step: 5);
                }
                else
                {
                    run.Status = RunStatus.Uploaded;
                    await store.UpdateRunAsync(run, ct);
                }
            }
            catch
            {
                run.Status = previousStatus;
                await store.UpdateRunAsync(run, CancellationToken.None);
                throw;
            }
        }
        finally
        {
            execLock.Release();
        }
    }

    public async Task StartValidationAsync(Guid runId, CancellationToken ct = default)
    {
        var execLock = GetExecutionLock(runId);
        if (!await execLock.WaitAsync(0, ct))
        {
            return;
        }

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<IMigrationStore>();
            var logger = scope.ServiceProvider.GetRequiredService<MigrationLogger>();
            var validation = scope.ServiceProvider.GetRequiredService<ValidationEngine>();

            var run = await store.GetRunAsync(runId, ct) ?? throw new KeyNotFoundException("Run not found.");
            var previousStatus = run.Status;
            run.Status = RunStatus.Validating;
            run.CurrentStep = 4;
            await store.UpdateRunAsync(run, ct);

            logger.Log(LogLevel.Information, "VALD-START", runId, "Starting verification pass on staged reports and SSRS source.", step: 4);

            var cred = _credStore.GetSourceCredential(runId.ToString());
            var adapter = SourceAdapterFactory.Create(run.SourceServerUrl, run.SourceApi, cred);
            using var adapterScope = adapter as IDisposable;
            var reports = await store.GetCatalogItemsAsync(runId, ItemType.Report, ct);

            var destToken = _credStore.GetDestinationToken();
            using var destClient = !string.IsNullOrEmpty(run.DestinationServerUrl) && !string.IsNullOrEmpty(destToken)
                ? new DestinationClient(run.DestinationServerUrl, destToken)
                : null;

            var stagedRenderer = scope.ServiceProvider.GetService<IStagedReportRenderer>();

            try
            {
                foreach (var r in reports)
                {
                    var record = await store.GetReportRecordAsync(r.Id, ct);
                    if (record == null || record.State == ReportState.Blocked || record.State == ReportState.Excluded) continue;

                    await validation.ValidateReportAsync(runId, r, record, adapter, destClient, stagedRenderer, ct);
                }

                run.Status = RunStatus.Validated;
                await store.UpdateRunAsync(run, ct);
            }
            catch
            {
                run.Status = previousStatus;
                await store.UpdateRunAsync(run, CancellationToken.None);
                throw;
            }
        }
        finally
        {
            execLock.Release();
        }
    }

    public async Task RetryFailedItemsAsync(Guid runId, CancellationToken ct = default)
    {
        using (var scope = _scopeFactory.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IMigrationStore>();
            var failedReports = await store.GetReportRecordsAsync(runId, ReportState.UploadFailed, ct: ct);
            foreach (var r in failedReports)
            {
                ReportStateMachine.Transition(r, ReportState.Queued);
                await store.UpdateReportRecordAsync(r, ct);
            }
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
            CleanupDesignerCache();
        }

        _credStore.ClearAll();
        MigrationMetrics.RunsTotal.Add(1, new KeyValuePair<string, object?>("event", "complete"));
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
            CleanupDesignerCache();
            _credStore.ClearAll();
            logger.Log(LogLevel.Warning, "RUN-ABANDON", runId, "Migration run abandoned by user.");
        }
    }

    private void CleanupDesignerCache()
    {
        try
        {
            var cacheDir = Path.Combine(Path.GetDirectoryName(_staging.GetRunStagingPath(Guid.Empty))!, "_designer-cache");
            if (Directory.Exists(cacheDir))
            {
                Directory.Delete(cacheDir, recursive: true);
            }
        }
        catch { }
    }
}
