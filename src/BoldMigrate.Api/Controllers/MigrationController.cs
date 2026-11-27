using System.Net;
using BoldMigrate.Core;
using BoldMigrate.Core.Dto;
using BoldMigrate.Core.Enums;
using BoldMigrate.Core.Interfaces;
using BoldMigrate.Core.Models;
using BoldMigrate.Core.StateMachine;
using BoldMigrate.Engine.DeltaSync;
using BoldMigrate.Engine.ExpressionTester;
using BoldMigrate.Engine.LocalFolder;
using BoldMigrate.Engine.Logging;
using BoldMigrate.Engine.Orchestrator;
using BoldMigrate.Engine.Preview;
using BoldMigrate.Engine.Upload;
using BoldMigrate.Infrastructure.Credentials;
using BoldMigrate.Infrastructure.SourceAdapters;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BoldMigrate.Api.Controllers;

[ApiController]
[Route("api/migration")]
public class MigrationController : ControllerBase
{
    private readonly IMigrationOrchestrator _orchestrator;
    private readonly IMigrationStore _store;
    private readonly ICredentialStore _credStore;
    private readonly MigrationLogger _logger;
    private readonly DeltaSyncEngine _deltaSync;
    private readonly LocalFolderMigrationService _localFolderService;
    private readonly ExpressionTesterEngine _expressionTester;
    private readonly IStagingStore _staging;
    private readonly BoldMigrate.Core.Configuration.MigrationSettings _settings;
    private readonly BoldMigrate.Engine.Validation.RenderValidationEngine _renderValidation;
    private readonly IStagedReportRenderer _stagedRenderer;
    private readonly IServiceScopeFactory _scopeFactory;

    public MigrationController(
        IMigrationOrchestrator orchestrator,
        IMigrationStore store,
        ICredentialStore credStore,
        MigrationLogger logger,
        DeltaSyncEngine deltaSync,
        LocalFolderMigrationService localFolderService,
        ExpressionTesterEngine expressionTester,
        IStagingStore staging,
        BoldMigrate.Core.Configuration.MigrationSettings settings,
        BoldMigrate.Engine.Validation.RenderValidationEngine renderValidation,
        IStagedReportRenderer stagedRenderer,
        IServiceScopeFactory scopeFactory)
    {
        _orchestrator = orchestrator;
        _store = store;
        _credStore = credStore;
        _logger = logger;
        _deltaSync = deltaSync;
        _localFolderService = localFolderService;
        _expressionTester = expressionTester;
        _staging = staging;
        _settings = settings;
        _renderValidation = renderValidation;
        _stagedRenderer = stagedRenderer;
        _scopeFactory = scopeFactory;
    }

    [HttpPost("runs")]
    public async Task<IActionResult> CreateOrGetRun([FromBody] CreateRunRequest req, CancellationToken ct)
    {
        if (req.ForceNew)
        {
            var current = await _orchestrator.GetActiveRunAsync(ct);
            if (current != null)
            {
                await _orchestrator.AbandonRunAsync(current.Id, ct);
            }
        }

        var run = await _orchestrator.CreateOrGetActiveRunAsync(req.Name ?? "SSRS Migration", req.SourceUrl ?? "http://localhost/ReportServer", ct);
        return Ok(run);
    }

    [HttpPut("runs/{id:guid}/credential-mode")]
    public async Task<IActionResult> SetCredentialMode(Guid id, [FromBody] CredentialModeRequest req, CancellationToken ct)
    {
        var run = await _store.GetRunAsync(id, ct);
        if (run == null) return NotFound();

        if (!Enum.TryParse<CredentialMode>(req.CredentialMode, ignoreCase: true, out var mode))
        {
            return BadRequest(new { message = $"Unsupported credential mode '{req.CredentialMode}'. Use InMemory or DpapiLocal." });
        }

        if (mode == CredentialMode.ExternalVault)
        {
            return BadRequest(new { message = "ExternalVault mode requires external KMS/KeyVault integration, which is not configured in this environment. Use InMemory or DpapiLocal." });
        }

        _credStore.SetMode(mode);
        run.CredentialMode = mode;
        await _store.UpdateRunAsync(run, ct);
        _logger.Log(LogLevel.Information, "CRED-MODE", id, $"Credential storage mode set to {mode}.");
        return Ok(new { success = true, credentialMode = mode.ToString() });
    }

    [HttpGet("runs/{id:guid}/permissions")]
    public async Task<IActionResult> GetPermissions(Guid id, CancellationToken ct)
    {
        if (await _store.GetRunAsync(id, ct) == null) return NotFound();
        return Ok(await _store.GetPermissionMappingsAsync(id, ct));
    }

    [HttpGet("runs/{id:guid}/subscriptions")]
    public async Task<IActionResult> GetSubscriptions(Guid id, CancellationToken ct)
    {
        if (await _store.GetRunAsync(id, ct) == null) return NotFound();
        return Ok(await _store.GetSubscriptionRecordsAsync(id, ct));
    }

    [HttpGet("runs/{id:guid}")]
    public async Task<IActionResult> GetRun(Guid id, CancellationToken ct)
    {
        var run = await _orchestrator.GetRunAsync(id, ct);
        if (run == null) return NotFound();
        return Ok(run);
    }

    [HttpGet("active-run")]
    public async Task<IActionResult> GetActiveRun(CancellationToken ct)
    {
        var run = await _orchestrator.GetActiveRunAsync(ct);
        return Ok(run);
    }

    [HttpPost("runs/{id:guid}/connect")]
    public async Task<IActionResult> TestConnect(Guid id, [FromBody] ConnectRequest req, CancellationToken ct)
    {
        var run = await _store.GetRunAsync(id, ct);
        if (run == null) return NotFound();

        if (!string.IsNullOrWhiteSpace(req.CredentialMode) &&
            Enum.TryParse<CredentialMode>(req.CredentialMode, ignoreCase: true, out var requestedMode) &&
            requestedMode != CredentialMode.ExternalVault)
        {
            _credStore.SetMode(requestedMode);
            run.CredentialMode = requestedMode;
        }

        var isBasicOverPlainHttp = string.Equals(req.AuthMode, "Basic", StringComparison.OrdinalIgnoreCase) &&
                                   Uri.TryCreate(req.SourceUrl, UriKind.Absolute, out var rawUri) &&
                                   rawUri.Scheme == Uri.UriSchemeHttp &&
                                   !rawUri.IsLoopback;
        if (isBasicOverPlainHttp)
        {
            var msg = ErrorCatalogue.FormatMessage(ErrorCatalogue.Src007);
            _logger.Log(LogLevel.Warning, ErrorCatalogue.Src007, id, msg);
            return BadRequest(new { success = false, errorCode = ErrorCatalogue.Src007, message = msg });
        }

        NetworkCredential? cred = null;
        var isIntegrated = string.Equals(req.AuthMode, "Integrated", StringComparison.OrdinalIgnoreCase) ||
                           (string.IsNullOrEmpty(req.Username) && string.IsNullOrEmpty(req.Password));

        if (!isIntegrated && !string.IsNullOrEmpty(req.Username) && !string.IsNullOrEmpty(req.Password))
        {
            if (req.Username.Contains('\\'))
            {
                var parts = req.Username.Split('\\', 2);
                cred = new NetworkCredential(parts[1], req.Password, parts[0]);
            }
            else if (req.Username.Contains('@'))
            {
                var parts = req.Username.Split('@', 2);
                cred = new NetworkCredential(parts[0], req.Password, parts[1]);
            }
            else
            {
                cred = new NetworkCredential(req.Username, req.Password);
            }
            _credStore.SetSourceCredential(id.ToString(), cred);
        }

        var resolvedUrl = BoldMigrate.Core.Common.HostResolver.Resolve(req.SourceUrl);
        var (adapter, api, probe) = await SourceAdapterFactory.CreateAndProbeWithResultAsync(resolvedUrl, cred, _settings.ExtractionPreferredApi, ct);
        using var adapterDisp = adapter as IDisposable;

        // If explicit credentials were provided and failed with 401 Unauthorized,
        // test whether Integrated Windows Authentication succeeds on this endpoint.
        if (!probe.Success && probe.ErrorCode == ErrorCatalogue.Src002 && cred != null)
        {
            try
            {
                var (intAdapter, _, intProbe) = await SourceAdapterFactory.CreateAndProbeWithResultAsync(resolvedUrl, null, _settings.ExtractionPreferredApi, ct);
                using var intAdapterDisp = intAdapter as IDisposable;
                if (intProbe.Success)
                {
                    _logger.Log(LogLevel.Warning, "CONN-FALLBACK", id, "Explicit credentials failed with 401, but Windows Integrated Authentication succeeded.");
                    return BadRequest(new
                    {
                        success = false,
                        errorCode = probe.ErrorCode,
                        message = $"{probe.ErrorMessage} Hint: Windows Integrated Authentication (Current User) succeeded on this server. Switch authentication to 'Windows Integrated (Current User)' to connect without entering a password."
                    });
                }
            }
            catch
            {
                // Ignore fallback probe failures
            }
        }

        if (probe.Success)
        {
            run.SourceServerUrl = req.SourceUrl;
            run.SourceApi = api;
            run.SourceVersion = probe.SourceVersion ?? "Unknown";
            run.Status = RunStatus.Connected;
            await _store.UpdateRunAsync(run, ct);

            _logger.Log(LogLevel.Information, "CONN-OK", id, $"Connected. {run.SourceVersion}, using {api}.");
            return Ok(new { success = true, version = run.SourceVersion, api = api.ToString() });
        }

        _logger.Log(LogLevel.Error, "CONN-FAIL", id, $"Connection failed: {probe.ErrorMessage}");
        return BadRequest(new { success = false, errorCode = probe.ErrorCode, message = probe.ErrorMessage });
    }

    [HttpPost("runs/{id:guid}/connect-destination")]
    public async Task<IActionResult> TestConnectDestination(Guid id, [FromBody] ConnectDestinationRequest req, CancellationToken ct)
    {
        var run = await _store.GetRunAsync(id, ct);
        if (run == null) return NotFound();

        try
        {
            using var destClient = new DestinationClient(req.DestinationUrl, req.BearerToken);
            string token;

            if (!string.IsNullOrWhiteSpace(req.BearerToken))
            {
                token = req.BearerToken;
                destClient.SetBearerToken(token);
            }
            else
            {
                token = await destClient.AuthenticateAsync(req.Email ?? "", req.Password ?? "", ct);
            }

            if (!string.IsNullOrEmpty(token))
            {
                run.DestinationServerUrl = req.DestinationUrl;
                run.DestinationUserEmail = req.Email ?? "BearerTokenAuth";
                await _store.UpdateRunAsync(run, ct);

                // Let SecurityMiddleware widen the CSP to this origin, otherwise the embedded
                // Bold Reports Viewer/Designer cannot reach the server they are pointed at.
                _settings.DestinationServerUrl = req.DestinationUrl;

                _credStore.SetDestinationToken(token);
                _logger.Log(LogLevel.Information, "DEST-OK", id, $"Connected to Bold Reports Server ({req.DestinationUrl})");
                return Ok(new
                {
                    success = true,
                    destinationUrl = req.DestinationUrl,
                    hasToken = true,
                    message = "Authenticated with Bold Reports Server"
                });
            }

            return BadRequest(new { success = false, message = "No token returned from Bold Reports Server" });
        }
        catch (Exception ex)
        {
            _logger.Log(LogLevel.Error, "DEST-FAIL", id, $"Bold Reports connection failed: {ex.Message}");
            return BadRequest(new { success = false, message = ex.Message });
        }
    }

    [HttpPost("runs/{id:guid}/fetch")]
    public async Task<IActionResult> FetchEstate(Guid id, CancellationToken ct)
    {
        if (await _store.GetRunAsync(id, ct) == null) return NotFound();
        QueueBackgroundWork(id, "DISC-ERROR", "Discovery", (orch, token) => orch.StartFetchAsync(id, token));
        return Accepted(new { message = "Discovery started" });
    }

    [HttpPost("runs/{id:guid}/check")]
    public async Task<IActionResult> CheckEstate(Guid id, CancellationToken ct)
    {
        if (await _store.GetRunAsync(id, ct) == null) return NotFound();
        QueueBackgroundWork(id, "XFRM-ERROR", "Check", (orch, token) => orch.StartCheckAsync(id, token));
        return Accepted(new { message = "Transform check started" });
    }

    [HttpPost("runs/{id:guid}/upload")]
    public async Task<IActionResult> StartUpload(Guid id, [FromQuery] string? action, [FromQuery] string? conflictPolicy = null, CancellationToken ct = default)
    {
        if (action == "pause")
        {
            _orchestrator.PauseRun(id);
            return Ok(new { status = "paused" });
        }
        if (action == "resume")
        {
            if (await _store.GetRunAsync(id, ct) == null) return NotFound();
            _orchestrator.ResumeRun(id);
            // Clearing the pause flag alone never restarted anything: the paused upload call had
            // already returned. Re-queue it so the remaining stages actually run.
            QueueBackgroundWork(id, "UPLD-ERROR", "Upload", (orch, token) => orch.StartUploadAsync(id, conflictPolicy, token));
            return Ok(new { status = "resumed" });
        }

        if (await _store.GetRunAsync(id, ct) == null) return NotFound();
        QueueBackgroundWork(id, "UPLD-ERROR", "Upload", (orch, token) => orch.StartUploadAsync(id, conflictPolicy, token));
        return Accepted(new { message = "Upload started", conflictPolicy = UploadLayer.ParseConflictPolicy(conflictPolicy ?? _settings.UploadConflictPolicy).ToString() });
    }

    [HttpPost("runs/{id:guid}/validate")]
    public async Task<IActionResult> StartValidation(Guid id, CancellationToken ct)
    {
        if (await _store.GetRunAsync(id, ct) == null) return NotFound();
        QueueBackgroundWork(id, "VALD-ERROR", "Validation", (orch, token) => orch.StartValidationAsync(id, token));
        return Accepted(new { message = "Validation started" });
    }

    [HttpPost("runs/{id:guid}/retry")]
    public async Task<IActionResult> RetryFailed(Guid id, CancellationToken ct)
    {
        if (await _store.GetRunAsync(id, ct) == null) return NotFound();
        QueueBackgroundWork(id, "RETRY-ERROR", "Retry", (orch, token) => orch.RetryFailedItemsAsync(id, token));
        return Accepted(new { message = "Retrying failed items" });
    }

    private void QueueBackgroundWork(Guid runId, string errorCode, string stageName, Func<IMigrationOrchestrator, CancellationToken, Task> work)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await work(_orchestrator, CancellationToken.None);
            }
            catch (Exception ex)
            {
                using var errScope = _scopeFactory.CreateScope();
                var appLogger = errScope.ServiceProvider.GetRequiredService<MigrationLogger>();
                appLogger.Log(LogLevel.Error, errorCode, runId, $"{stageName} failed: {Redactor.RedactSecrets(ex.Message)}");
            }
        });
    }

    [HttpPost("runs/{id:guid}/abandon")]
    public async Task<IActionResult> Abandon(Guid id, CancellationToken ct)
    {
        await _orchestrator.AbandonRunAsync(id, ct);
        return Ok(new { status = "abandoned" });
    }

    [HttpGet("runs/{id:guid}/estate")]
    public async Task<IActionResult> GetEstate(Guid id, [FromQuery] int skip = 0, [FromQuery] int take = 100, CancellationToken ct = default)
    {
        var items = await _store.GetCatalogItemsAsync(id, ct: ct);
        var clampedTake = Math.Clamp(take, 1, 1000);
        var clampedSkip = Math.Max(0, skip);
        return Ok(new
        {
            totalCount = items.Count,
            skip = clampedSkip,
            take = clampedTake,
            folders = items.Count(i => i.ItemType == ItemType.Folder),
            dataSources = items.Count(i => i.ItemType == ItemType.DataSource),
            datasets = items.Count(i => i.ItemType == ItemType.DataSet),
            reports = items.Count(i => i.ItemType == ItemType.Report),
            items = items.Skip(clampedSkip).Take(clampedTake)
        });
    }

    [HttpGet("runs/{id:guid}/datasources")]
    public async Task<IActionResult> GetDataSources(Guid id, CancellationToken ct)
    {
        var items = await _store.GetCatalogItemsAsync(id, ItemType.DataSource, ct);
        return Ok(items.Select(i =>
        {
            var cred = _credStore.GetDataSourceCredential(i.Id.ToString());
            SharedDataSourceDefinition? def = null;
            if (StagedCatalog.IsStaged(i) && System.IO.File.Exists(i.StagedFilePath))
            {
                try
                {
                    def = SharedDataSourceDefinition.Parse(System.IO.File.ReadAllBytes(i.StagedFilePath));
                }
                catch { }
            }

            var retrieval = cred != null ? "Store" : (def?.CredentialRetrieval ?? "Prompt");
            var isWindows = (cred != null && !string.IsNullOrEmpty(cred.Domain)) || (def?.WindowsCredentials ?? false) || string.Equals(retrieval, "Integrated", StringComparison.OrdinalIgnoreCase);

            return new
            {
                id = i.Id,
                name = i.Name,
                sourcePath = i.SourcePath,
                parentPath = i.ParentPath,
                sizeBytes = i.SizeBytes,
                serverType = def?.Extension ?? "SQL",
                connectString = Redactor.RedactConnectionString(def?.ConnectString ?? ""),
                hasConnectString = !string.IsNullOrWhiteSpace(def?.ConnectString),
                credentialRetrieval = retrieval,
                isWindowsCredentials = isWindows,
                hasCredentials = cred != null || string.Equals(retrieval, "Integrated", StringComparison.OrdinalIgnoreCase) || string.Equals(retrieval, "None", StringComparison.OrdinalIgnoreCase),
                userName = cred == null ? (def?.UserName) : (string.IsNullOrEmpty(cred.Domain) ? cred.UserName : $"{cred.Domain}\\{cred.UserName}")
            };
        }));
    }

    [HttpGet("runs/{id:guid}/reports")]
    public async Task<IActionResult> GetReports(Guid id, CancellationToken ct)
    {
        var records = await _store.GetReportRecordsAsync(id, ct: ct);
        var items = await _store.GetCatalogItemsAsync(id, ct: ct);
        var itemMap = items.ToDictionary(i => i.Id);

        var result = records.Select(r =>
        {
            itemMap.TryGetValue(r.CatalogItemId, out var item);
            return new
            {
                id = r.CatalogItemId,
                name = item?.Name ?? "Unnamed Report",
                folder = item?.ParentPath ?? "/",
                path = item?.SourcePath ?? "",
                sizeBytes = item?.SizeBytes ?? 0,
                status = r.State.ToString(),
                bucket = r.Bucket.ToString(),
                blockReason = r.BlockReasonCode,
                featureTags = r.FeatureTags,
                parameterCount = r.ParameterCount,
                dataSourceRefs = r.DataSourceRefs,
                uploadAttempts = r.UploadAttempts
            };
        });

        return Ok(result);
    }

    [HttpPost("runs/{id:guid}/select-reports")]
    public async Task<IActionResult> SelectReports(Guid id, [FromBody] SelectReportsRequest req, CancellationToken ct)
    {
        // A missing or unparsable selectedReportIds would otherwise silently exclude every
        // report and make the next upload look like a success that migrated nothing.
        if (req?.SelectedReportIds == null)
        {
            return BadRequest(new
            {
                success = false,
                message = "selectedReportIds is required. Send an empty array to deselect every report."
            });
        }

        var records = await _store.GetReportRecordsAsync(id, ct: ct);
        var selectedSet = new HashSet<Guid>(req.SelectedReportIds);

        foreach (var r in records)
        {
            if (r.Bucket == ReportBucket.Blocked) continue;

            // Routed through the state machine (invariant 4). Restoring the pre-exclusion state
            // means de-selecting and re-selecting an approved report no longer throws its
            // validation away and force it back through Step 3.
            if (selectedSet.Contains(r.CatalogItemId))
            {
                if (r.State == ReportState.Excluded)
                {
                    ReportStateMachine.RestoreFromDeselect(r);
                    await _store.UpdateReportRecordAsync(r, ct);
                }
            }
            else
            {
                if (r.State != ReportState.Excluded)
                {
                    ReportStateMachine.ExcludeForDeselect(r);
                    await _store.UpdateReportRecordAsync(r, ct);
                }
            }
        }

        _logger.Log(LogLevel.Information, "REP-SELECT", id, $"Updated selection: {selectedSet.Count} reports selected for migration.");
        return Ok(new { success = true, selectedCount = selectedSet.Count });
    }

    [HttpGet("runs/{id:guid}/reports/{reportId:guid}/rdl")]
    public async Task<IActionResult> GetReportRdl(Guid id, Guid reportId, CancellationToken ct)
    {
        var item = await _store.GetCatalogItemAsync(reportId, ct);
        if (item == null || item.RunId != id) return NotFound(new { message = "Report not found" });

        var record = await _store.GetReportRecordAsync(reportId, ct);

        string originalRdl = "";
        string transformedRdl = "";

        if (!string.IsNullOrWhiteSpace(item.StagedFilePath) && System.IO.File.Exists(item.StagedFilePath))
        {
            transformedRdl = await System.IO.File.ReadAllTextAsync(item.StagedFilePath, ct);
        }

        var originalPath = _staging.ResolveOriginalPath(id, item.SourcePath);
        if (!string.IsNullOrWhiteSpace(originalPath) && System.IO.File.Exists(originalPath))
        {
            originalRdl = await System.IO.File.ReadAllTextAsync(originalPath, ct);
        }
        else
        {
            originalRdl = transformedRdl;
        }

        return Ok(new
        {
            id = item.Id,
            name = item.Name,
            folder = item.ParentPath,
            path = item.SourcePath,
            sizeBytes = item.SizeBytes,
            schemaVersion = record?.RdlSchemaVersion ?? "2016",
            bucket = record?.Bucket.ToString() ?? "Clean",
            blockReason = record?.BlockReasonCode,
            parameterCount = record?.ParameterCount ?? 0,
            originalRdl,
            transformedRdl
        });
    }

    /// <summary>
    /// Returns the most recent measured validation result for a report, or null when the
    /// report has not been validated yet. The UI must show "not validated" rather than an
    /// assumed score when this is null.
    /// </summary>
    [HttpGet("runs/{id:guid}/reports/{reportId:guid}/validation")]
    public async Task<IActionResult> GetReportValidation(Guid id, Guid reportId, CancellationToken ct)
    {
        var record = await _store.GetReportRecordAsync(reportId, ct);
        var results = await _store.GetValidationResultsAsync(reportId, ct);
        var latest = results.OrderByDescending(r => r.RunUtc).FirstOrDefault();

        if (latest == null)
        {
            return Ok(new
            {
                validated = false,
                state = record?.State.ToString() ?? "Unknown",
                message = "This report has not been validated yet. Run validation to compare the "
                          + "source and destination renders."
            });
        }

        return Ok(new
        {
            validated = true,
            state = record?.State.ToString() ?? "Unknown",
            dataAccuracy = latest.DataAccuracy,
            visualSimilarity = latest.VisualSimilarity,
            rowsSource = latest.RowsSource,
            rowsDestination = latest.RowsDestination,
            layoutShifts = latest.LayoutShifts,
            sourcePageCount = latest.SourcePageCount,
            destinationPageCount = latest.DestinationPageCount,
            renderSummary = latest.RenderSummary,
            renderStatus = latest.RenderStatus,
            renderDurationMs = latest.RenderDurationMs,
            screenshotRelativePath = latest.ScreenshotRelativePath,
            errorMessage = latest.ErrorMessage,
            parameterSetHash = latest.ParameterSetHash,
            runUtc = latest.RunUtc
        });
    }

    public record ValidateRenderRequest(List<Guid>? ReportIds);

    [HttpPost("runs/{id:guid}/validate-render")]
    public async Task<IActionResult> RunAutomatedRenderValidation(
        Guid id,
        [FromQuery] int concurrency = 5,
        [FromBody] ValidateRenderRequest? req = null,
        CancellationToken ct = default)
    {
        var baseUrl = $"{Request.Scheme}://{Request.Host}";
        var targetIds = req?.ReportIds;
        var countMsg = targetIds != null && targetIds.Count > 0 ? $"{targetIds.Count} selected report(s)" : "all reports";
        _logger.Log(LogLevel.Information, "VALD-RENDER", id, $"Starting automated render validation for {countMsg} (concurrency: {concurrency}) via {baseUrl}");

        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var engine = scope.ServiceProvider.GetRequiredService<BoldMigrate.Engine.Validation.RenderValidationEngine>();
                var appLogger = scope.ServiceProvider.GetRequiredService<MigrationLogger>();
                await engine.ValidateRunAsync(id, baseUrl, concurrency, CancellationToken.None, targetIds);
                appLogger.Log(LogLevel.Information, "VALD-COMPLETE", id, "Automated render validation completed.");
            }
            catch (Exception ex)
            {
                using var errScope = _scopeFactory.CreateScope();
                var appLogger = errScope.ServiceProvider.GetRequiredService<MigrationLogger>();
                appLogger.Log(LogLevel.Error, "VALD-FAILED", id, $"Automated render validation failed: {ex.Message}");
            }
        });

        return Accepted(new { message = "Automated render validation started", concurrency });
    }

    [HttpPost("runs/{id:guid}/skip-validation")]
    public async Task<IActionResult> SkipValidation(Guid id, CancellationToken ct)
    {
        await _renderValidation.SkipValidationAsync(id, ct);
        _logger.Log(LogLevel.Information, "VALD-SKIP", id, "Validation skipped by user request; reports marked as ready for upload.");
        return Ok(new { message = "Validation skipped. Reports approved for upload." });
    }

    private sealed record ValidationSummaryCardDto(
        Guid ReportId,
        string Name,
        string Path,
        string Status,
        string State,
        long? DurationMs,
        string? ScreenshotUrl,
        string? ErrorMessage,
        string? Summary,
        List<string> FeatureTags,
        int ParameterCount);

    [HttpGet("runs/{id:guid}/validation-summary")]
    public async Task<IActionResult> GetValidationSummary(Guid id, CancellationToken ct)
    {
        var reports = await _store.GetCatalogItemsAsync(id, ItemType.Report, ct);
        var records = (await _store.GetReportRecordsAsync(id, ct: ct))
            .ToDictionary(r => r.CatalogItemId);
        var latestByReport = (await _store.GetValidationResultsByRunAsync(id, ct))
            .GroupBy(v => v.ReportId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(v => v.RunUtc).First());

        var cards = new List<ValidationSummaryCardDto>(reports.Count);

        foreach (var report in reports)
        {
            records.TryGetValue(report.Id, out var record);
            latestByReport.TryGetValue(report.Id, out var latest);

            var featureTags = (record?.FeatureTags ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();

            var status = latest?.RenderStatus ?? (record?.State == ReportState.AutoPassed ? "Passed" :
                                                record?.State == ReportState.Approved ? "Approved" :
                                                record?.State == ReportState.AwaitingReview ? "NeedsManualCheck" :
                                                record?.State == ReportState.ValidationFailed ? "Failed" : "NotValidated");

            cards.Add(new ValidationSummaryCardDto(
                report.Id,
                report.Name,
                report.SourcePath,
                status,
                record?.State.ToString() ?? "Unknown",
                latest?.RenderDurationMs,
                latest?.ScreenshotRelativePath,
                latest?.ErrorMessage,
                latest?.RenderSummary,
                featureTags,
                record?.ParameterCount ?? 0));
        }

        var total = cards.Count;
        var passed = cards.Count(c => c.Status is "Passed" or "Approved");
        var dsOffline = cards.Count(c => c.Status == "DataSourceOffline");
        var failed = cards.Count(c => c.Status == "Failed");
        var needsManual = cards.Count(c => c.Status == "NeedsManualCheck");
        var skipped = cards.Count(c => c.Status == "Skipped");

        return Ok(new
        {
            runId = id,
            total,
            passed,
            dataSourceOffline = dsOffline,
            failed,
            needsManualCheck = needsManual,
            skipped,
            reports = cards
        });
    }

    [HttpDelete("runs/{id:guid}/validation-results")]
    public async Task<IActionResult> ClearValidationResults(Guid id, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BoldMigrate.Infrastructure.Data.MigrationDbContext>();

        var reportIds = await db.CatalogItems
            .Where(c => c.RunId == id && c.ItemType == ItemType.Report)
            .Select(c => c.Id)
            .ToListAsync(ct);

        if (reportIds.Count > 0)
        {
            var oldResults = await db.ValidationResults
                .Where(v => reportIds.Contains(v.ReportId))
                .ToListAsync(ct);
            if (oldResults.Count > 0)
            {
                db.ValidationResults.RemoveRange(oldResults);
            }

            var records = await db.ReportRecords
                .Where(r => reportIds.Contains(r.CatalogItemId) &&
                            (r.State == ReportState.Approved ||
                             r.State == ReportState.AutoPassed ||
                             r.State == ReportState.AwaitingReview ||
                             r.State == ReportState.ValidationFailed))
                .ToListAsync(ct);
            foreach (var rec in records)
            {
                ReportStateMachine.TryTransition(rec, ReportState.Analysed);
            }

            await db.SaveChangesAsync(ct);
        }

        // Remove cached screenshot PNGs inside staging store
        var screenshotsDir = Path.Combine(_staging.GetRunStagingPath(id), "screenshots");
        if (Directory.Exists(screenshotsDir))
        {
            try { Directory.Delete(screenshotsDir, recursive: true); } catch { }
        }

        return Ok(new { cleared = true });
    }

    [HttpGet("runs/{id:guid}/screenshots/{reportId:guid}.png")]
    public IActionResult GetScreenshot(Guid id, Guid reportId)
    {
        var filePath = Path.Combine(_staging.GetRunStagingPath(id), "screenshots", $"{reportId}.png");
        if (!System.IO.File.Exists(filePath))
        {
            return NotFound();
        }

        return PhysicalFile(filePath, "image/png");
    }

    [HttpGet("runs/{id:guid}/review-queue")]
    public async Task<IActionResult> GetReviewQueue(Guid id, CancellationToken ct)
    {
        var queue = (await _store.GetReportRecordsAsync(id, ReportState.AwaitingReview, ct: ct))
            .OrderBy(r => r.Bucket)
            .ToList();
        return Ok(queue);
    }

    [HttpPost("runs/{id:guid}/reports/{reportId:guid}/approve")]
    public async Task<IActionResult> ApproveReport(Guid id, Guid reportId, [FromBody] ApproveRequest req, CancellationToken ct)
    {
        var record = await _store.GetReportRecordAsync(reportId, ct);
        if (record == null) return NotFound();

        ReportStateMachine.TryTransition(record, ReportState.Approved);
        await _store.UpdateReportRecordAsync(record, ct);

        await _store.AddApprovalAsync(new Approval
        {
            Id = Guid.NewGuid(),
            ReportId = reportId,
            Decision = "Approved",
            UserId = req.UserId ?? "Administrator",
            UserName = req.UserName ?? "Administrator",
            DecidedUtc = DateTime.UtcNow,
            Comment = req.Comment ?? "Approved via review queue"
        }, ct);

        _logger.Log(LogLevel.Information, "VALD-APPROVE", id, $"Report '{reportId}' approved by {req.UserName ?? "Administrator"}.");
        return Ok(new { success = true });
    }

    [HttpPost("runs/{id:guid}/reports/{reportId:guid}/flag")]
    public async Task<IActionResult> FlagReport(Guid id, Guid reportId, [FromBody] ApproveRequest req, CancellationToken ct)
    {
        var record = await _store.GetReportRecordAsync(reportId, ct);
        if (record == null) return NotFound();

        ReportStateMachine.TryTransition(record, ReportState.Flagged);
        await _store.UpdateReportRecordAsync(record, ct);

        _logger.Log(LogLevel.Information, "VALD-FLAG", id, $"Report '{reportId}' flagged for review: {req.Comment}");
        return Ok(new { success = true });
    }

    [HttpPost("runs/{id:guid}/reports/bulk-approve")]
    public async Task<IActionResult> BulkApprove(Guid id, [FromQuery] decimal minAccuracy = 99.0m, CancellationToken ct = default)
    {
        var queue = await _store.GetReportRecordsAsync(id, ReportState.AwaitingReview, ct: ct);
        var approvedCount = 0;

        foreach (var r in queue)
        {
            var results = await _store.GetValidationResultsAsync(r.CatalogItemId, ct);
            var latest = results.OrderByDescending(v => v.RunUtc).FirstOrDefault();
            if (latest != null && latest.DataAccuracy >= minAccuracy)
            {
                ReportStateMachine.TryTransition(r, ReportState.Approved);
                await _store.UpdateReportRecordAsync(r, ct);
                approvedCount++;
            }
            else
            {
                // Spec rule: Must stop at first report below threshold rather than skipping it!
                break;
            }
        }

        return Ok(new { approved = approvedCount });
    }

    [HttpPost("runs/{id:guid}/delta-sync")]
    public async Task<IActionResult> DeltaSync(Guid id, CancellationToken ct)
    {
        var run = await _store.GetRunAsync(id, ct);
        if (run == null) return NotFound(new { message = "Run not found." });

        if (string.IsNullOrWhiteSpace(run.DestinationServerUrl))
        {
            return BadRequest(new { message = "Destination server URL is not configured for this run." });
        }

        var cred = _credStore.GetSourceCredential(id.ToString());
        var source = SourceAdapterFactory.Create(run.SourceServerUrl, run.SourceApi, cred);
        using var sourceDisp = source as IDisposable;

        var token = _credStore.GetDestinationToken();
        using var dest = new DestinationClient(run.DestinationServerUrl, token);
        var res = await _deltaSync.RunDeltaSyncAsync(id, source, dest, ct);
        return Ok(res);
    }

    [HttpGet("runs/{id:guid}/cutover")]
    public async Task<IActionResult> GetCutoverGates(Guid id, CancellationToken ct)
    {
        var reports = await _store.GetReportRecordsAsync(id, ct: ct);
        var catalogReports = await _store.GetCatalogItemsAsync(id, ItemType.Report, ct);
        var validatedReportIds = (await _store.GetValidationResultsByRunAsync(id, ct))
            .Select(v => v.ReportId)
            .ToHashSet();

        var uploadedItems = catalogReports.Where(r => IsGenuineDestinationId(r.DestinationItemId)).ToList();
        var actuallyUploaded = uploadedItems.Count;

        var selected = reports.Where(r => r.State != ReportState.Excluded).ToList();
        var awaitingReview = selected.Count(r => r.State == ReportState.AwaitingReview);
        var uploadFailed = selected.Count(r => r.State == ReportState.UploadFailed);
        var validationFailed = selected.Count(r => r.State == ReportState.ValidationFailed);
        var notStarted = selected.Count(r => r.State is ReportState.Discovered or ReportState.Analysed or ReportState.Queued);

        // A report counts as validated only when a measured validation result exists for it.
        var validatedCount = uploadedItems.Count(item => validatedReportIds.Contains(item.Id));

        var allReady = actuallyUploaded > 0
                       && validatedCount == actuallyUploaded
                       && awaitingReview == 0
                       && uploadFailed == 0
                       && validationFailed == 0
                       && notStarted == 0;

        var gates = new[]
        {
            new
            {
                name = "Reports uploaded to destination",
                passed = actuallyUploaded > 0,
                detail = actuallyUploaded > 0
                    ? $"{actuallyUploaded} of {catalogReports.Count} reports uploaded to Bold Reports"
                    : "0 reports uploaded. Complete Step 5 (Upload Pipeline) with valid Bold Reports credentials first."
            },
            new
            {
                name = "Every uploaded report validated against destination",
                passed = actuallyUploaded > 0 && validatedCount == actuallyUploaded,
                detail = actuallyUploaded == 0
                    ? "No reports uploaded yet"
                    : $"{validatedCount} of {actuallyUploaded} uploaded reports have a measured source-vs-destination result"
            },
            new
            {
                name = "No reports awaiting manual review",
                passed = awaitingReview == 0,
                detail = awaitingReview == 0
                    ? "Nothing queued for review"
                    : $"{awaitingReview} report(s) flagged for manual review"
            },
            new
            {
                name = "No failed uploads or validations",
                passed = uploadFailed == 0 && validationFailed == 0,
                detail = uploadFailed == 0 && validationFailed == 0
                    ? "No failures recorded"
                    : $"{uploadFailed} upload failure(s), {validationFailed} validation failure(s)"
            },
            new
            {
                name = "No reports left unprocessed",
                passed = notStarted == 0,
                detail = notStarted == 0
                    ? "All selected reports have been through the pipeline"
                    : $"{notStarted} selected report(s) have not been uploaded yet"
            },
            new
            {
                name = "URL compatibility shim configured",
                passed = _settings.UrlShimEnabled,
                detail = _settings.UrlShimEnabled
                    ? $"Shim enabled on {_settings.UrlShimListenPath}"
                    : "Disabled in Settings. Optional — only needed to keep existing SSRS bookmarks working."
            }
        };

        // The shim gate is advisory: a team that is not preserving old SSRS URLs can cut over
        // without it, so it is reported but does not gate readiness.
        return Ok(new
        {
            ready = allReady,
            gates,
            gateCount = gates.Length,
            advisoryGates = new[] { "URL compatibility shim configured" }
        });
    }

    [HttpPost("runs/{id:guid}/cutover")]
    public async Task<IActionResult> ConfirmCutover(Guid id, CancellationToken ct)
    {
        var catalogReports = await _store.GetCatalogItemsAsync(id, ItemType.Report, ct);
        var actuallyUploaded = catalogReports.Count(r => IsGenuineDestinationId(r.DestinationItemId));
        if (actuallyUploaded == 0)
        {
            return BadRequest(new
            {
                success = false,
                message = "Cannot cutover: 0 reports have been uploaded to Bold Reports. Connect to Bold Reports in Step 1 and execute Step 5: Upload Pipeline first."
            });
        }

        await _orchestrator.CompleteCutoverAsync(id, ct);
        return Ok(new { success = true, status = "CutOver" });
    }

    [HttpGet("runs/{id:guid}/log")]
    public IActionResult GetLiveLog(Guid id)
    {
        return Ok(_logger.GetLiveLog(id));
    }

    [HttpGet("runs/{id:guid}/poll")]
    public async Task<IActionResult> PollState(Guid id, CancellationToken ct)
    {
        var run = await _store.GetRunAsync(id, ct);
        if (run == null) return NotFound();

        var reports = await _store.GetReportRecordsAsync(id, ct: ct);
        var items = await _store.GetCatalogItemsAsync(id, ct: ct);

        return Ok(new
        {
            status = run.Status.ToString(),
            currentStep = run.CurrentStep,
            itemsTotal = items.Count,
            foldersCount = items.Count(i => i.ItemType == ItemType.Folder),
            datasourcesCount = items.Count(i => i.ItemType == ItemType.DataSource),
            datasetsCount = items.Count(i => i.ItemType == ItemType.DataSet),
            reportsTotal = reports.Count,
            reportsUploaded = reports.Count(r => r.State == ReportState.Uploaded),
            reportsValidated = reports.Count(r => r.State is ReportState.AutoPassed or ReportState.Approved or ReportState.AwaitingReview or ReportState.ValidationFailed or ReportState.Flagged or ReportState.Uploaded),
            reportsAutoPassed = reports.Count(r => r.State == ReportState.AutoPassed),
            reportsAwaitingReview = reports.Count(r => r.State == ReportState.AwaitingReview),
            reportsApproved = reports.Count(r => r.State == ReportState.Approved),
            reportsFailed = reports.Count(r => r.State is ReportState.UploadFailed or ReportState.ValidationFailed),
            reportsClean = reports.Count(r => r.Bucket == ReportBucket.Clean),
            reportsAutoFixed = reports.Count(r => r.Bucket == ReportBucket.AutoFixed),
            reportsNeedsReview = reports.Count(r => r.Bucket == ReportBucket.NeedsReview),
            reportsBlocked = reports.Count(r => r.Bucket == ReportBucket.Blocked)
        });
    }

    [HttpPost("local-folder")]
    public async Task<IActionResult> RunLocalFolderMigration([FromBody] LocalFolderMigrationRequest req, CancellationToken ct)
    {
        var result = await _localFolderService.RunMigrationAsync(req, ct);
        return Ok(result);
    }

    [HttpGet("local-folder/logs")]
    public IActionResult GetLocalFolderLogs()
    {
        return Ok(_localFolderService.CurrentLogs);
    }

    [HttpPost("expression/evaluate")]
    public IActionResult EvaluateExpression([FromBody] ExpressionTestRequest req)
    {
        var result = _expressionTester.Evaluate(req);
        return Ok(result);
    }

    [HttpGet("expression/samples")]
    public IActionResult GetExpressionSamples()
    {
        return Ok(ExpressionTesterEngine.GetSampleExpressions());
    }

    /// <summary>
    /// Step 4: every staged report with the shared data sources and datasets it uses, as they
    /// resolve inside the staged SSRS folder tree. Drives the local Viewer / Designer list.
    /// </summary>
    [HttpGet("runs/{id:guid}/preview/reports")]
    public async Task<IActionResult> GetPreviewReports(Guid id, CancellationToken ct)
    {
        if (await _store.GetRunAsync(id, ct) == null) return NotFound();

        var catalog = new StagedCatalog(id, await _store.GetCatalogItemsAsync(id, ct: ct));
        var records = (await _store.GetReportRecordsAsync(id, ct: ct)).ToDictionary(r => r.CatalogItemId);

        var reports = new List<object>();
        foreach (var report in catalog.OfType(ItemType.Report).OrderBy(r => r.SourcePath, StringComparer.OrdinalIgnoreCase))
        {
            records.TryGetValue(report.Id, out var record);
            var staged = StagedCatalog.IsStaged(report);
            var folder = StagedCatalog.ParentOf(report.SourcePath);
            var deps = staged ? ReportDependencies.Read(await System.IO.File.ReadAllBytesAsync(report.StagedFilePath!, ct)) : new ReportDependencies();

            var dataSources = deps.DataSources.Select(ds =>
            {
                if (ds.SharedReference == null)
                {
                    return new { name = ds.Name, kind = "Embedded", reference = (string?)null, resolvedPath = (string?)null, resolved = true, credentials = "InReport" };
                }

                var shared = catalog.ResolveReference(ds.SharedReference, folder, ItemType.DataSource);
                return new
                {
                    name = ds.Name,
                    kind = "Shared",
                    reference = (string?)ds.SharedReference,
                    resolvedPath = shared != null ? StagedCatalog.NormalizePath(shared.SourcePath) : null,
                    resolved = shared != null && StagedCatalog.IsStaged(shared),
                    credentials = shared == null ? "Unresolved" : DescribeCredentials(shared)
                };
            }).ToList();

            var dataSets = deps.SharedDataSets.Select(dset =>
            {
                var shared = catalog.ResolveReference(dset.SharedReference, folder, ItemType.DataSet);
                return new
                {
                    name = dset.Name,
                    reference = dset.SharedReference,
                    resolvedPath = shared != null ? StagedCatalog.NormalizePath(shared.SourcePath) : null,
                    resolved = shared != null && StagedCatalog.IsStaged(shared)
                };
            }).ToList();

            reports.Add(new
            {
                id = report.Id,
                name = report.Name,
                folder,
                // The SSRS path is what the viewer and designer open.
                path = StagedCatalog.NormalizePath(report.SourcePath),
                staged,
                status = record?.State.ToString() ?? "Discovered",
                bucket = record?.Bucket.ToString() ?? "Clean",
                uploaded = IsGenuineDestinationId(report.DestinationItemId),
                // SQLite drops DateTimeKind; mark it UTC so clients do not read it as local time.
                localEditUtc = record?.LocalEditUtc is { } edited ? DateTime.SpecifyKind(edited, DateTimeKind.Utc) : (DateTime?)null,
                localEditPendingUpload = record?.LocalEditPendingUpload ?? false,
                dataSources,
                dataSets,
                unresolvedCount = dataSources.Count(d => !d.resolved) + dataSets.Count(d => !d.resolved)
            });
        }

        return Ok(new
        {
            viewerServiceUrl = $"/api/preview/runs/{id}/viewer",
            designerServiceUrl = $"/api/preview/runs/{id}/designer",
            reports
        });
    }

    /// <summary>
    /// Step 1: tests connectivity for a shared data source and, if valid, saves the credentials
    /// and connection string. Returns detailed error information if the connection fails.
    /// </summary>
    [HttpPost("runs/{id:guid}/datasources/{dataSourceId:guid}/validate-and-save")]
    public async Task<IActionResult> ValidateAndSaveDataSourceCredentials(
        Guid id,
        Guid dataSourceId,
        [FromBody] DataSourceCredentialRequest req,
        CancellationToken ct)
    {
        var item = await _store.GetCatalogItemAsync(dataSourceId, ct);
        if (item == null || item.RunId != id || item.ItemType != ItemType.DataSource) return NotFound();

        var mode = ResolveCredentialMode(req);
        SharedDataSourceDefinition? stagedDef = null;
        if (!string.IsNullOrEmpty(item.StagedFilePath) && System.IO.File.Exists(item.StagedFilePath))
        {
            try
            {
                stagedDef = SharedDataSourceDefinition.Parse(await System.IO.File.ReadAllBytesAsync(item.StagedFilePath, ct));
            }
            catch
            {
                // Ignore parse failures here; connection string validation will report if empty
            }
        }

        var effectiveConnectString = !string.IsNullOrWhiteSpace(req.ConnectString)
            ? req.ConnectString.Trim()
            : (stagedDef?.ConnectString ?? string.Empty);

        var (ok, message, detail, certUntrusted) = await TestDataSourceConnectionAsync(
            dataSourceId,
            effectiveConnectString,
            mode,
            req.UserName,
            req.Password,
            stagedDef?.Extension,
            req.TrustServerCertificate,
            ct);

        if (!ok)
        {
            _logger.Log(LogLevel.Warning, "CONN-DS-FAIL", id, $"Validation failed for data source '{item.Name}': {message}", itemPath: item.SourcePath);
            return BadRequest(new { success = false, message, detail, certUntrusted });
        }

        var hasCredentials = PersistDataSourceCredentials(id, item, req, mode, effectiveConnectString);
        _logger.Log(LogLevel.Information, "CONN-DS-CRED", id, $"Validated and saved credentials ({mode}) for data source '{item.Name}'.", itemPath: item.SourcePath);
        return Ok(new { success = true, hasCredentials, credentialRetrieval = mode, message });
    }

    /// <summary>
    /// Step 1: stores the authentication configuration for a shared data source (Prompt, Store, Integrated, None),
    /// optional user/password, and optional updated connection string.
    /// Updates both the in-memory credential store and the staged .rsds definition.
    /// </summary>
    [HttpPut("runs/{id:guid}/datasources/{dataSourceId:guid}/credentials")]
    public async Task<IActionResult> SetDataSourceCredentials(Guid id, Guid dataSourceId, [FromBody] DataSourceCredentialRequest req, CancellationToken ct)
    {
        var item = await _store.GetCatalogItemAsync(dataSourceId, ct);
        if (item == null || item.RunId != id || item.ItemType != ItemType.DataSource) return NotFound();

        var mode = ResolveCredentialMode(req);
        var hasCredentials = PersistDataSourceCredentials(id, item, req, mode);

        _logger.Log(LogLevel.Information, "CONN-DS-CRED", id, $"Credentials updated ({mode}) for data source '{item.Name}'.", itemPath: item.SourcePath);
        return Ok(new { success = true, hasCredentials, credentialRetrieval = mode });
    }

    private static string ResolveCredentialMode(DataSourceCredentialRequest req)
    {
        var mode = req.CredentialRetrieval?.Trim();
        if (string.IsNullOrEmpty(mode))
        {
            mode = !string.IsNullOrWhiteSpace(req.UserName) ? "Store" : "Prompt";
        }
        return mode;
    }

    private async Task<(bool Ok, string Message, string? Detail, bool CertUntrusted)> TestDataSourceConnectionAsync(
        Guid dataSourceId,
        string connectString,
        string mode,
        string? userName,
        string? password,
        string? extension,
        bool? explicitTrustCert,
        CancellationToken ct)
    {
        if (string.Equals(mode, "Prompt", StringComparison.OrdinalIgnoreCase))
        {
            return (true, "Saved in Prompt mode (credentials will be requested at runtime).", null, false);
        }

        if (string.IsNullOrWhiteSpace(connectString))
        {
            return (false, "Connection string is required.", "Enter a valid SQL Server connection string before validating.", false);
        }

        if (!BoldMigrate.Core.Common.SqlConnectionHelper.IsSqlServerConnection(connectString, extension))
        {
            return (true, $"Saved ({extension} server validation is skipped; verified format only).", null, false);
        }

        try
        {
            var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(connectString)
            {
                ConnectTimeout = 5
            };

            if (explicitTrustCert == true)
            {
                builder.TrustServerCertificate = true;
            }
            else if (BoldMigrate.Core.Common.SqlConnectionHelper.ShouldTrustSqlCertificate(
                         connectString, extension, _settings.PreviewAllowUntrustedSqlCert))
            {
                builder.TrustServerCertificate = true;
            }

            if (string.Equals(mode, "Store", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(userName))
                {
                    return (false, "Username is required for 'Store (Saved Account)' mode.", "Enter a SQL username (for example: sa) and password.", false);
                }

                var effectivePassword = password;
                if (string.IsNullOrEmpty(effectivePassword))
                {
                    var existing = _credStore.GetDataSourceCredential(dataSourceId.ToString());
                    if (existing != null)
                    {
                        effectivePassword = existing.Password;
                    }
                }

                if (string.IsNullOrEmpty(effectivePassword))
                {
                    return (false, "Password is required for 'Store (Saved Account)' mode.", "Enter the password for this SQL account.", false);
                }

                builder.IntegratedSecurity = false;
                builder.UserID = userName.Trim();
                builder.Password = effectivePassword;
            }
            else if (string.Equals(mode, "Integrated", StringComparison.OrdinalIgnoreCase))
            {
                builder.IntegratedSecurity = true;
                builder.Remove("User ID");
                builder.Remove("Password");
            }

            await using var conn = new Microsoft.Data.SqlClient.SqlConnection(builder.ConnectionString);
            await conn.OpenAsync(ct);
            var dbName = string.IsNullOrWhiteSpace(conn.Database) ? "default" : conn.Database;
            return (true, $"Connection verified (SQL Server {conn.ServerVersion}, database '{dbName}') and saved.", null, false);
        }
        catch (Microsoft.Data.SqlClient.SqlException ex)
        {
            var redacted = Redactor.RedactSecrets(ex.Message);
            var isCertIssue = BoldMigrate.Core.Common.SqlConnectionHelper.IsUntrustedCertificateSqlError(ex.Number, ex.Message);
            var friendlyMsg = isCertIssue
                ? "SQL Server presented a TLS certificate issued by an authority that is not trusted. Enable 'Trust self-signed SQL Server certificates' and retry."
                : redacted;
            return (false, friendlyMsg, $"SQL Error {ex.Number} (State {ex.State}, Class {ex.Class}): {redacted}", isCertIssue);
        }
        catch (Exception ex)
        {
            var redacted = Redactor.RedactSecrets(ex.Message);
            return (false, redacted, $"{ex.GetType().Name}: {redacted}", false);
        }
    }

    private bool PersistDataSourceCredentials(Guid runId, CatalogItem item, DataSourceCredentialRequest req, string mode, string? effectiveConnectString = null)
    {
        bool hasCredentials;

        if (string.Equals(mode, "Integrated", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(mode, "None", StringComparison.OrdinalIgnoreCase))
        {
            _credStore.RemoveDataSourceCredential(item.Id.ToString());
            hasCredentials = true;
        }
        else if (string.Equals(mode, "Prompt", StringComparison.OrdinalIgnoreCase))
        {
            _credStore.RemoveDataSourceCredential(item.Id.ToString());
            hasCredentials = false;
        }
        else // Store
        {
            if (!string.IsNullOrWhiteSpace(req.UserName))
            {
                var user = req.UserName.Trim();
                var effectivePass = req.Password;
                if (string.IsNullOrEmpty(effectivePass))
                {
                    effectivePass = _credStore.GetDataSourceCredential(item.Id.ToString())?.Password ?? "";
                }
                var cred = user.Contains('\\')
                    ? new NetworkCredential(user.Split('\\', 2)[1], effectivePass, user.Split('\\', 2)[0])
                    : new NetworkCredential(user, effectivePass);
                _credStore.SetDataSourceCredential(item.Id.ToString(), cred);
                hasCredentials = true;
            }
            else
            {
                _credStore.RemoveDataSourceCredential(item.Id.ToString());
                hasCredentials = false;
            }
        }

        // Update staged RSDS/RDS file if available
        if (!string.IsNullOrEmpty(item.StagedFilePath) && System.IO.File.Exists(item.StagedFilePath))
        {
            try
            {
                var doc = BoldMigrate.Core.Common.SafeXml.LoadFile(item.StagedFilePath);
                var root = doc.Root;
                if (root != null)
                {
                    var ns = root.Name.Namespace;
                    var props = root.Name.LocalName switch
                    {
                        "RptDataSource" => root.Element(ns + "ConnectionProperties"),
                        "DataSourceDefinition" => root,
                        _ => null
                    };
                    if (props != null)
                    {
                        var updatedConnStr = !string.IsNullOrWhiteSpace(req.ConnectString)
                            ? req.ConnectString.Trim()
                            : effectiveConnectString;
                        if (req.TrustServerCertificate == true &&
                            !string.IsNullOrWhiteSpace(updatedConnStr) &&
                            BoldMigrate.Core.Common.SqlConnectionHelper.IsSqlServerConnection(updatedConnStr) &&
                            !BoldMigrate.Core.Common.SqlConnectionHelper.HasTrustServerCertificateSetting(updatedConnStr))
                        {
                            updatedConnStr = updatedConnStr.TrimEnd(';') + ";TrustServerCertificate=True;";
                        }

                        if (!string.IsNullOrWhiteSpace(updatedConnStr) &&
                            (!string.IsNullOrWhiteSpace(req.ConnectString) || req.TrustServerCertificate == true))
                        {
                            var csElem = props.Element(ns + "ConnectString");
                            if (csElem == null)
                            {
                                csElem = new System.Xml.Linq.XElement(ns + "ConnectString");
                                props.Add(csElem);
                            }
                            csElem.Value = updatedConnStr;
                        }

                        var crElem = props.Element(ns + "CredentialRetrieval");
                        if (crElem == null)
                        {
                            crElem = new System.Xml.Linq.XElement(ns + "CredentialRetrieval");
                            props.Add(crElem);
                        }
                        crElem.Value = mode;

                        var winElem = props.Element(ns + "WindowsCredentials");
                        if (winElem == null)
                        {
                            winElem = new System.Xml.Linq.XElement(ns + "WindowsCredentials");
                            props.Add(winElem);
                        }
                        winElem.Value = (string.Equals(mode, "Integrated", StringComparison.OrdinalIgnoreCase) ||
                                        (!string.IsNullOrEmpty(req.UserName) && req.UserName.Contains('\\')))
                                        ? "True" : "False";

                        doc.Save(item.StagedFilePath);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Log(LogLevel.Warning, "DS-XML-UPDATE", runId, $"Failed to update staged data source XML: {ex.Message}");
            }
        }

        return hasCredentials;
    }

    private string DescribeCredentials(CatalogItem dataSource)
    {
        if (_credStore.GetDataSourceCredential(dataSource.Id.ToString()) != null) return "Stored";
        if (!StagedCatalog.IsStaged(dataSource)) return "Unresolved";

        var def = SharedDataSourceDefinition.Parse(System.IO.File.ReadAllBytes(dataSource.StagedFilePath!));
        return def?.CredentialRetrieval switch
        {
            "Integrated" => "Integrated",
            "None" => "None",
            _ => "PromptAtRunTime"
        };
    }

    private static bool IsGenuineDestinationId(string? id) =>
        !string.IsNullOrEmpty(id) &&
        !id.StartsWith("dest-", StringComparison.OrdinalIgnoreCase) &&
        !id.StartsWith("rep-", StringComparison.OrdinalIgnoreCase) &&
        !id.StartsWith("existing-", StringComparison.OrdinalIgnoreCase);

    [HttpGet("runs/{id:guid}/designer-config")]
    public async Task<IActionResult> GetDesignerConfig(Guid id, CancellationToken ct)
    {
        var run = await _store.GetRunAsync(id, ct);
        if (run == null) return NotFound();

        var destUrl = !string.IsNullOrWhiteSpace(run.DestinationServerUrl) ? run.DestinationServerUrl : "http://localhost:8090/reporting/site/site1";
        using var client = new DestinationClient(destUrl);
        var tenantId = client.GetTenantId();
        var token = _credStore.GetDestinationToken();

        var origin = "http://localhost:8090";
        if (Uri.TryCreate(destUrl, UriKind.Absolute, out var uri))
        {
            origin = $"{uri.Scheme}://{uri.Authority}".TrimEnd('/');
        }

        // Security: never expose raw Bearer JWT tokens over GET endpoints.
        return Ok(new
        {
            serviceUrl = $"{origin}/reporting/reportservice/api/Designer",
            cloudServiceUrl = "https://service.boldreports.com/api/Designer",
            reportServerUrl = $"{origin}/reporting/api/site/{tenantId}",
            serviceAuthorizationToken = string.Empty,
            hasAuthorizationToken = !string.IsNullOrEmpty(token),
            tenantId,
            serverUrl = origin
        });
    }

    /// <summary>
    /// Configuration for embedding the Bold Reports Viewer / Designer against the DESTINATION
    /// server, built from the run plus the stored admin token.
    ///
    /// The widgets talk to Bold Reports directly from the browser, so per the embedding API they
    /// need serviceUrl, reportServerUrl and a bearer token client-side — the front end previously
    /// guessed these URLs (producing paths like /reporting/api/reporting/reportservice/api/Viewer)
    /// and relied on a token held only in a JS variable, which was empty after any page refresh.
    ///
    /// This is POST, not GET, so SecurityMiddleware's CSRF + same-origin check applies before a
    /// token is handed out, and so the response is never cached or replayable from a URL.
    /// </summary>
    [HttpPost("runs/{id:guid}/embed-config")]
    public async Task<IActionResult> GetEmbedConfig(Guid id, CancellationToken ct)
    {
        var run = await _store.GetRunAsync(id, ct);
        if (run == null) return NotFound();

        if (string.IsNullOrWhiteSpace(run.DestinationServerUrl))
        {
            return BadRequest(new { message = "Connect a Bold Reports destination server first (Step 1)." });
        }

        if (!Uri.TryCreate(run.DestinationServerUrl, UriKind.Absolute, out var uri))
        {
            return BadRequest(new { message = $"Destination server URL '{run.DestinationServerUrl}' is not a valid absolute URL." });
        }

        var token = _credStore.GetDestinationToken();
        if (string.IsNullOrWhiteSpace(token))
        {
            return BadRequest(new
            {
                message = "No Bold Reports session token is held. Re-connect the destination server in Step 1."
            });
        }

        using var client = new DestinationClient(run.DestinationServerUrl, token);
        var tenantId = client.GetTenantId();
        var origin = $"{uri.Scheme}://{uri.Authority}".TrimEnd('/');

        return Ok(new
        {
            origin,
            tenantId,
            reportServerUrl = $"{origin}/reporting/api/site/{tenantId}",
            viewerServiceUrl = $"{origin}/reporting/reportservice/api/Viewer",
            designerServiceUrl = $"{origin}/reporting/reportservice/api/Designer",
            // The embedding model requires this in the browser; it is the operator's own session
            // token, released only over a same-origin, CSRF-checked POST.
            serviceAuthorizationToken = $"bearer {token}"
        });
    }

    /// <summary>
    /// Per-report upload outcome, with a plain reason for every report that did NOT reach the
    /// destination.
    ///
    /// The upload gate silently skips reports in states like AwaitingReview or ValidationFailed,
    /// logging UPLD-GATE-SKIP into a collapsed execution drawer. Operators saw "9 reports" become
    /// "6 uploaded" with nothing explaining the other three. This is derived from persisted state,
    /// so it survives a refresh and is correct even for a run uploaded in an earlier session.
    /// </summary>
    [HttpGet("runs/{id:guid}/upload/outcome")]
    public async Task<IActionResult> GetUploadOutcome(Guid id, CancellationToken ct)
    {
        var run = await _store.GetRunAsync(id, ct);
        if (run == null) return NotFound();

        var items = (await _store.GetCatalogItemsAsync(id, ItemType.Report, ct)).ToList();
        var records = (await _store.GetReportRecordsAsync(id, ct: ct))
            .ToDictionary(r => r.CatalogItemId, r => r);

        var uploaded = new List<object>();
        var notUploaded = new List<object>();

        foreach (var item in items)
        {
            records.TryGetValue(item.Id, out var rec);
            var state = rec?.State ?? ReportState.Discovered;

            if (IsGenuineDestinationId(item.DestinationItemId))
            {
                uploaded.Add(new { reportId = item.Id, name = item.Name, path = item.SourcePath, state = state.ToString() });
                continue;
            }

            var (reason, fix) = DescribeNotUploaded(state, rec?.Bucket ?? ReportBucket.Clean, rec?.ExcludedReason);

            notUploaded.Add(new
            {
                reportId = item.Id,
                name = item.Name,
                path = item.SourcePath,
                state = state.ToString(),
                reason,
                fix
            });
        }

        return Ok(new
        {
            total = items.Count,
            uploadedCount = uploaded.Count,
            notUploadedCount = notUploaded.Count,
            uploaded,
            notUploaded
        });
    }

    /// <summary>Plain-language reason a report is not on the destination, plus the way to fix it.</summary>
    private static (string Reason, string Fix) DescribeNotUploaded(ReportState state, ReportBucket bucket, string? excludedReason)
    {
        if (bucket == ReportBucket.Blocked)
        {
            return ("Blocked by a compatibility rule — it cannot run on Bold Reports as-is.",
                    "Open the compatibility note in Step 2 and edit the report, or exclude it from this migration.");
        }

        return state switch
        {
            ReportState.Excluded => (
                string.IsNullOrWhiteSpace(excludedReason)
                    ? "De-selected in Step 2, so it was never queued."
                    : $"Excluded: {excludedReason}",
                "Re-select it in Step 2 to include it."),

            ReportState.AwaitingReview => (
                "Waiting for manual review — validation could not confirm it renders correctly (usually prompt parameters with no default).",
                "Open it in Step 3, check the preview, then Approve it. Or use Skip Validation to accept it as-is."),

            ReportState.ValidationFailed => (
                "Render validation failed, so the upload gate held it back.",
                "Open the failure snapshot in Step 3 to see what the viewer showed, fix the report, then re-validate."),

            ReportState.Flagged => (
                "Flagged for designer attention during review.",
                "Edit it in the Step 3 designer, then Approve it."),

            ReportState.UploadFailed => (
                "The upload to Bold Reports failed.",
                "Use Retry Failed in Step 4; check the execution log for the server's error."),

            ReportState.Uploading => (
                "Left mid-upload by a restart, so its outcome is unknown.",
                "Use Retry Failed in Step 4 to re-upload it."),

            ReportState.Discovered or ReportState.Staged or ReportState.Analysed or ReportState.Queued => (
                "Never validated, and the upload gate only accepts validated or approved reports.",
                "Run validation in Step 3, or use Skip Validation, then upload again."),

            _ => ($"Not uploaded (state: {state}).",
                  "Re-run validation in Step 3, then upload again.")
        };
    }

    [HttpGet("runs/{id:guid}/destination/sections")]
    public async Task<IActionResult> GetSections(Guid id, CancellationToken ct)
    {
        var run = await _store.GetRunAsync(id, ct);
        if (run == null) return NotFound();

        var reports = await _store.GetCatalogItemsAsync(id, ItemType.Report, ct);
        var records = (await _store.GetReportRecordsAsync(id, ct: ct)).ToDictionary(r => r.CatalogItemId);

        // Check live items on Bold Reports Server if connected with an active session token
        List<DestinationItemInfo>? liveRemoteReports = null;
        if (!string.IsNullOrWhiteSpace(run.DestinationServerUrl))
        {
            try
            {
                var token = _credStore.GetDestinationToken();
                if (!string.IsNullOrEmpty(token))
                {
                    using var client = new DestinationClient(run.DestinationServerUrl, token);
                    var tenantId = client.GetTenantId();
                    liveRemoteReports = await client.GetItemsAsync(tenantId, "Report", ct);
                }
            }
            catch { }
        }

        var groups = reports
            .GroupBy(r => string.IsNullOrEmpty(r.ParentPath) ? "/" : r.ParentPath)
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var secName = g.Key.Trim('/');
                var remoteInSec = liveRemoteReports?.Count(lr =>
                    string.Equals(lr.CategoryName, secName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(lr.CategoryName, g.Key, StringComparison.OrdinalIgnoreCase)) ?? 0;

                return new
                {
                    section = g.Key,
                    totalReports = g.Count(),
                    uploadedReports = Math.Max(remoteInSec, g.Count(r => !string.IsNullOrEmpty(r.DestinationItemId) ||
                        (records.TryGetValue(r.Id, out var rec) && rec.State == ReportState.Uploaded))),
                    remoteLiveCount = remoteInSec,
                    reports = g.Select(r => new
                    {
                        id = r.Id,
                        name = r.Name,
                        path = r.SourcePath,
                        destinationItemId = r.DestinationItemId,
                        isUploaded = !string.IsNullOrEmpty(r.DestinationItemId) || (liveRemoteReports?.Any(lr => string.Equals(lr.Name, r.Name, StringComparison.OrdinalIgnoreCase)) ?? false)
                    }).ToList()
                };
            })
            .ToList();

        return Ok(new
        {
            sections = groups,
            totalLiveServerReports = liveRemoteReports?.Count ?? 0
        });
    }

    [HttpPost("runs/{id:guid}/destination/delete-reports")]
    public async Task<IActionResult> DeleteDestinationReports(Guid id, [FromBody] DeleteDestinationReportsRequest req, CancellationToken ct)
    {
        var run = await _store.GetRunAsync(id, ct);
        if (run == null) return NotFound(new { success = false, message = "Run not found" });

        var hasScope = req.DeleteAll || (req.Sections?.Length > 0) || (req.ReportIds?.Length > 0);
        if (!hasScope)
            return BadRequest(new { success = false, message = "Specify ReportIds, Sections, or set DeleteAll to true." });

        var token = _credStore.GetDestinationToken();
        if (string.IsNullOrWhiteSpace(run.DestinationServerUrl) || string.IsNullOrEmpty(token))
            return BadRequest(new { success = false, message = "Not connected to the Bold Reports destination. Connect (sign in) first, then retry." });

        using var client = new DestinationClient(run.DestinationServerUrl, token);
        var tenantId = client.GetTenantId();

        var allReports = (await _store.GetCatalogItemsAsync(id, ItemType.Report, ct)).ToList();
        var records = (await _store.GetReportRecordsAsync(id, ct: ct)).ToDictionary(r => r.CatalogItemId);

        List<DestinationItemInfo> live;
        try
        {
            live = await client.GetItemsAsync(tenantId, "Report", ct);
        }
        catch (Exception ex)
        {
            return StatusCode(502, new { success = false, message = $"Failed to list reports on Bold Reports: {ex.Message}" });
        }

        // Which local reports are in scope, and which remote reports must be deleted.
        List<CatalogItem> localTargets;
        var remoteTargets = new Dictionary<string, DestinationItemInfo>();

        if (req.DeleteAll)
        {
            localTargets = allReports;
            foreach (var r in live) remoteTargets[r.Id] = r;
        }
        else if (req.Sections?.Length > 0)
        {
            var sections = req.Sections.Select(s => s.Trim().Trim('/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
            localTargets = allReports.Where(r => sections.Contains((r.ParentPath ?? "").Trim('/'))).ToList();
            foreach (var r in live.Where(r => !string.IsNullOrEmpty(r.CategoryName) && sections.Contains(r.CategoryName.Trim('/'))))
                remoteTargets[r.Id] = r;
            foreach (var t in localTargets) AddRemoteMatch(t, live, remoteTargets);
        }
        else
        {
            var idSet = new HashSet<Guid>(req.ReportIds!);
            localTargets = allReports.Where(r => idSet.Contains(r.Id)).ToList();
            foreach (var t in localTargets) AddRemoteMatch(t, live, remoteTargets);
        }

        var deletedIds = new HashSet<string>();
        var failedIds = new HashSet<string>();
        var errors = new List<string>();

        foreach (var remote in remoteTargets.Values)
        {
            try
            {
                if (await client.DeleteItemAsync(tenantId, remote.Id, ct)) deletedIds.Add(remote.Id);
                else { failedIds.Add(remote.Id); errors.Add($"Server refused to delete '{remote.Name}'."); }
            }
            catch (Exception ex)
            {
                failedIds.Add(remote.Id);
                errors.Add($"Error deleting '{remote.Name}': {ex.Message}");
            }
        }

        // Reset local state only for reports whose remote copy is gone (or never existed).
        foreach (var r in localTargets)
        {
            var remoteId = !string.IsNullOrEmpty(r.DestinationItemId) ? r.DestinationItemId
                : live.FirstOrDefault(l => string.Equals(l.Name, r.Name, StringComparison.OrdinalIgnoreCase))?.Id;
            if (remoteId != null && failedIds.Contains(remoteId)) continue;

            r.DestinationItemId = null;
            await _store.UpdateCatalogItemAsync(r, ct);
            if (records.TryGetValue(r.Id, out var rec))
            {
                ReportStateMachine.TryTransition(rec, ReportState.Analysed);
                rec.UploadAttempts = 0;
                await _store.UpdateReportRecordAsync(rec, ct);
            }
        }

        // Delete categories only when everything else succeeded.
        if (req.DeleteCategories && errors.Count == 0)
        {
            try
            {
                var liveCats = await client.GetItemsAsync(tenantId, "Category", ct);
                var protectedCats = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "General", "Sample Reports", "Solution Reports" };
                foreach (var cat in liveCats.Where(c => !protectedCats.Contains(c.Name)))
                {
                    if (req.DeleteAll || (req.Sections?.Any(s => s.Trim().Trim('/').Equals(cat.Name, StringComparison.OrdinalIgnoreCase)) ?? false))
                    {
                        try { await client.DeleteItemAsync(tenantId, cat.Id, ct); }
                        catch (Exception ex) { errors.Add($"Could not delete category '{cat.Name}': {ex.Message}"); }
                    }
                }
            }
            catch (Exception ex)
            {
                errors.Add($"Could not list categories: {ex.Message}");
            }
        }

        var scopeDesc = req.DeleteAll ? "All reports" :
                        req.Sections?.Length > 0 ? $"{req.Sections.Length} section(s)" :
                        $"{localTargets.Count} selected report(s)";

        var level = errors.Count > 0 ? LogLevel.Warning : LogLevel.Information;
        _logger.Log(level, "DEST-DEL", id, $"Bulk delete ({scopeDesc}): {deletedIds.Count} deleted, {failedIds.Count} failed.");
        foreach (var e in errors) _logger.Log(LogLevel.Warning, "DEST-DEL", id, e);

        return Ok(new
        {
            success = errors.Count == 0,
            deletedCount = deletedIds.Count,
            failedCount = failedIds.Count,
            scope = scopeDesc,
            message = errors.Count == 0
                ? $"Deleted {deletedIds.Count} report(s) ({scopeDesc}) from Bold Reports Server."
                : $"Deleted {deletedIds.Count} report(s); {errors.Count} problem(s). See details.",
            errors = errors.Count > 0 ? errors : null
        });
    }

    private static void AddRemoteMatch(CatalogItem local, List<DestinationItemInfo> live, Dictionary<string, DestinationItemInfo> into)
    {
        var match = !string.IsNullOrEmpty(local.DestinationItemId)
            ? live.FirstOrDefault(l => l.Id == local.DestinationItemId)
            : live.FirstOrDefault(l => string.Equals(l.Name, local.Name, StringComparison.OrdinalIgnoreCase));
        if (match != null) into[match.Id] = match;
    }

    /// <summary>
    /// Feature 1: Bulk Data Source Host / Database Remapping (+ optional bulk credentials).
    /// Supports DryRun preview or persisting updated .rsds XML and credential vault entries.
    /// </summary>
    [HttpPost("runs/{id:guid}/datasources/bulk-remap")]
    public async Task<IActionResult> BulkRemapDataSources(Guid id, [FromBody] BulkRemapDataSourcesRequest req, CancellationToken ct)
    {
        if (await _store.GetRunAsync(id, ct) == null) return NotFound(new { message = "Run not found." });

        var dataSources = (await _store.GetCatalogItemsAsync(id, ItemType.DataSource, ct)).ToList();
        if (req.DataSourceIds != null && req.DataSourceIds.Length > 0)
        {
            var idSet = req.DataSourceIds.ToHashSet();
            dataSources = dataSources.Where(d => idSet.Contains(d.Id)).ToList();
        }

        var previews = new List<object>();
        var updatedCount = 0;
        var changedCount = 0;

        foreach (var ds in dataSources)
        {
            SharedDataSourceDefinition? def = null;
            if (!string.IsNullOrEmpty(ds.StagedFilePath) && System.IO.File.Exists(ds.StagedFilePath))
            {
                try { def = SharedDataSourceDefinition.Parse(await System.IO.File.ReadAllBytesAsync(ds.StagedFilePath, ct)); }
                catch { }
            }

            var before = def?.ConnectString ?? string.Empty;
            var after = BoldMigrate.Core.Common.SqlConnectionHelper.RemapConnectionString(
                before,
                req.FindServer,
                req.ReplaceServer,
                req.FindDatabase,
                req.ReplaceDatabase,
                req.RawFind,
                req.RawReplace,
                req.TrustServerCertificate);

            var hasConnChange = !string.Equals(before, after, StringComparison.Ordinal);
            var hasCredOverride = !string.IsNullOrWhiteSpace(req.CredentialRetrieval);
            var changed = hasConnChange || hasCredOverride;
            if (changed) changedCount++;

            if (!req.DryRun && changed)
            {
                var effectiveMode = !string.IsNullOrWhiteSpace(req.CredentialRetrieval)
                    ? req.CredentialRetrieval.Trim()
                    : (def?.CredentialRetrieval ?? (_credStore.GetDataSourceCredential(ds.Id.ToString()) != null ? "Store" : "Prompt"));

                var existingCred = _credStore.GetDataSourceCredential(ds.Id.ToString());
                var effectiveUser = !string.IsNullOrWhiteSpace(req.UserName)
                    ? req.UserName
                    : (!string.IsNullOrEmpty(existingCred?.Domain) ? $"{existingCred.Domain}\\{existingCred.UserName}" : existingCred?.UserName);
                var effectivePass = !string.IsNullOrEmpty(req.Password) ? req.Password : existingCred?.Password;

                var credReq = new DataSourceCredentialRequest(effectiveUser, effectivePass, effectiveMode, after, req.TrustServerCertificate);
                PersistDataSourceCredentials(id, ds, credReq, effectiveMode, after);
                updatedCount++;
            }

            previews.Add(new
            {
                id = ds.Id,
                name = ds.Name,
                before,
                after,
                changed
            });
        }

        if (!req.DryRun && updatedCount > 0)
        {
            _logger.Log(LogLevel.Information, "CONN-DS-BULK", id, $"Bulk remapped {updatedCount} data source(s).");
        }

        return Ok(new
        {
            success = true,
            dryRun = req.DryRun,
            total = previews.Count,
            updatedCount = req.DryRun ? changedCount : updatedCount,
            items = previews
        });
    }

    /// <summary>
    /// Feature 2: Pre-flight dry-run preview of Step 5 upload actions under the selected conflict policy.
    /// </summary>
    [HttpPost("runs/{id:guid}/upload/dry-run")]
    public async Task<IActionResult> DryRunUpload(Guid id, [FromQuery] string? conflictPolicy = null, CancellationToken ct = default)
    {
        var run = await _store.GetRunAsync(id, ct);
        if (run == null) return NotFound(new { message = "Run not found." });

        var destUrl = !string.IsNullOrWhiteSpace(run.DestinationServerUrl) ? run.DestinationServerUrl : run.SourceServerUrl;
        if (string.IsNullOrWhiteSpace(destUrl))
            return BadRequest(new { message = "Destination Bold Reports URL is not configured for this run." });

        var policy = UploadLayer.ParseConflictPolicy(conflictPolicy ?? _settings.UploadConflictPolicy);
        var token = _credStore.GetDestinationToken();
        using var destClient = new DestinationClient(destUrl, token);
        using var scope = _scopeFactory.CreateScope();
        var upload = scope.ServiceProvider.GetRequiredService<UploadLayer>();

        var plan = await upload.PreviewUploadPlanAsync(id, destClient, policy, ct);
        return Ok(plan);
    }

    /// <summary>
    /// Feature 3: Verifies all staged Subreport, Shared DataSet, and Shared DataSource links.
    /// </summary>
    [HttpGet("runs/{id:guid}/dependencies/verify")]
    public async Task<IActionResult> VerifyDependencies(Guid id, CancellationToken ct)
    {
        if (await _store.GetRunAsync(id, ct) == null) return NotFound();
        var items = await _store.GetCatalogItemsAsync(id, ct: ct);
        var summary = DependencyVerifier.VerifyCatalog(id, items);
        return Ok(summary);
    }

    /// <summary>
    /// Feature 3: Rebinds a broken DataSourceReference, SharedDataSetReference, or Subreport ReportName in staged RDL XML.
    /// </summary>
    [HttpPost("runs/{id:guid}/reports/{reportId:guid}/rebind-reference")]
    public async Task<IActionResult> RebindReference(Guid id, Guid reportId, [FromBody] RebindReferenceRequest req, CancellationToken ct)
    {
        var item = await _store.GetCatalogItemAsync(reportId, ct);
        if (item == null || item.RunId != id || item.ItemType != ItemType.Report) return NotFound(new { message = "Report not found." });
        if (string.IsNullOrEmpty(item.StagedFilePath) || !System.IO.File.Exists(item.StagedFilePath))
            return BadRequest(new { message = "Staged RDL file not found." });
        if (string.IsNullOrWhiteSpace(req.NewReference))
            return BadRequest(new { message = "NewReference is required." });

        var originalBytes = await System.IO.File.ReadAllBytesAsync(item.StagedFilePath, ct);
        var (updated, updatedBytes) = DependencyVerifier.RebindReferenceInRdl(originalBytes, req.ReferenceType, req.OldReference ?? string.Empty, req.NewReference);
        if (!updated)
            return BadRequest(new { message = $"Matching {req.ReferenceType} reference '{req.OldReference}' was not found in '{item.Name}'." });

        await System.IO.File.WriteAllBytesAsync(item.StagedFilePath, updatedBytes, ct);
        var record = await _store.GetReportRecordAsync(reportId, ct);
        if (record != null)
        {
            record.LocalEditUtc = DateTime.UtcNow;
            if (!string.IsNullOrEmpty(item.DestinationItemId)) record.LocalEditPendingUpload = true;
            await _store.UpdateReportRecordAsync(record, ct);
        }

        _logger.Log(LogLevel.Information, "DEP-REBIND", id, $"Rebound {req.ReferenceType} '{req.OldReference}' -> '{req.NewReference}' in '{item.Name}'.", itemPath: item.SourcePath);
        return Ok(new { success = true, message = $"Rebound '{req.OldReference}' to '{req.NewReference}'." });
    }

    /// <summary>
    /// Feature 4: Captures/serves an SSRS source render PNG for side-by-side slider & pixel-diff comparison.
    /// </summary>
    [HttpGet("runs/{id:guid}/screenshots/{reportId:guid}/source.png")]
    public async Task<IActionResult> GetSourceScreenshot(Guid id, Guid reportId, [FromQuery] bool refresh = false, CancellationToken ct = default)
    {
        var run = await _store.GetRunAsync(id, ct);
        if (run == null) return NotFound(new { available = false, reason = "Run not found." });

        var report = await _store.GetCatalogItemAsync(reportId, ct);
        if (report == null || report.RunId != id || report.ItemType != ItemType.Report)
            return NotFound(new { available = false, reason = "Report not found." });

        var dir = Path.Combine(_staging.GetRunStagingPath(id), "screenshots");
        var sourcePngPath = Path.Combine(dir, $"{reportId}-source.png");
        if (!refresh && System.IO.File.Exists(sourcePngPath))
        {
            return PhysicalFile(sourcePngPath, "image/png");
        }

        if (string.IsNullOrWhiteSpace(run.SourceServerUrl))
            return NotFound(new { available = false, reason = "Source SSRS server URL is not configured (local-folder run)." });

        try
        {
            var cred = _credStore.GetSourceCredential(id.ToString());
            var source = SourceAdapterFactory.Create(run.SourceServerUrl, run.SourceApi, cred);
            using var disp = source as IDisposable;

            var renderParams = new Dictionary<string, string> { ["rc:OutputFormat"] = "PNG", ["rc:StartPage"] = "1" };
            var bytes = await source.RenderAsync(report.SourcePath, "IMAGE", renderParams, ct);

            // Check PNG signature (89 50 4E 47)
            if (bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
            {
                Directory.CreateDirectory(dir);
                await System.IO.File.WriteAllBytesAsync(sourcePngPath, bytes, ct);
                return File(bytes, "image/png");
            }

            return StatusCode(502, new { available = false, reason = "SSRS IMAGE output was not PNG (may require parameters or non-PNG TIFF fallback)." });
        }
        catch (Exception ex)
        {
            return StatusCode(502, new { available = false, reason = $"SSRS source render unavailable: {ex.Message}" });
        }
    }

    /// <summary>
    /// Compares the DATA behind the SSRS render and the staged local render of the same report.
    ///
    /// The swipe / side-by-side / pixel-diff views answer "do these look the same", but a pixel
    /// diff cannot separate "the table shifted a few pixels" from "the query returned different
    /// rows" — both light up red. This exports both sides to CSV and diffs the records, so the
    /// operator gets a straight answer: layout only, reordered, changed values, or missing rows.
    /// </summary>
    [HttpGet("runs/{id:guid}/reports/{reportId:guid}/data-diff")]
    public async Task<IActionResult> GetReportDataDiff(Guid id, Guid reportId, CancellationToken ct = default)
    {
        var run = await _store.GetRunAsync(id, ct);
        if (run == null) return NotFound(new { available = false, reason = "Run not found." });

        var report = await _store.GetCatalogItemAsync(reportId, ct);
        if (report == null || report.RunId != id || report.ItemType != ItemType.Report)
            return NotFound(new { available = false, reason = "Report not found." });

        if (string.IsNullOrWhiteSpace(run.SourceServerUrl))
        {
            return NotFound(new
            {
                available = false,
                reason = "This run has no SSRS source server (local-folder import), so there is nothing to compare against."
            });
        }

        string sourceCsv;
        try
        {
            var cred = _credStore.GetSourceCredential(id.ToString());
            var source = SourceAdapterFactory.Create(run.SourceServerUrl, run.SourceApi, cred);
            using var disp = source as IDisposable;

            var bytes = await source.RenderAsync(report.SourcePath, "CSV", new Dictionary<string, string>(), ct);
            sourceCsv = System.Text.Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex)
        {
            return StatusCode(502, new
            {
                available = false,
                reason = $"Could not export the SSRS report as CSV: {Redactor.RedactSecrets(ex.Message)}"
            });
        }

        string stagedCsv;
        try
        {
            var bytes = await _stagedRenderer.RenderStagedReportAsync(
                id, report.SourcePath, "CSV", new Dictionary<string, string>(), ct);

            if (bytes == null || bytes.Length == 0)
            {
                return StatusCode(502, new
                {
                    available = false,
                    reason = "The staged report produced no CSV output. Run validation first, or check the data source credentials."
                });
            }

            stagedCsv = System.Text.Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex)
        {
            return StatusCode(502, new
            {
                available = false,
                reason = $"Could not export the staged report as CSV: {Redactor.RedactSecrets(ex.Message)}"
            });
        }

        var diff = BoldMigrate.Engine.Validation.DataDiffEngine.Compare(
            sourceCsv, stagedCsv, _settings.ValidationSampleRowLimit);

        return Ok(new
        {
            available = true,
            reportName = report.Name,
            reportPath = report.SourcePath,
            verdict = diff.Verdict.ToString(),
            summary = diff.Summary,
            isLayoutOnlyDifference = diff.IsLayoutOnlyDifference,
            sourceColumns = diff.SourceColumns,
            targetColumns = diff.TargetColumns,
            sourceRowCount = diff.SourceRowCount,
            targetRowCount = diff.TargetRowCount,
            cellDifferences = diff.CellDifferences,
            onlyInSource = diff.OnlyInSource,
            onlyInTarget = diff.OnlyInTarget,
            truncated = diff.Truncated
        });
    }
}

public record CreateRunRequest(string? Name, string? SourceUrl, bool ForceNew = false);
public record CredentialModeRequest(string CredentialMode);
public record ConnectRequest(string SourceUrl, string? Username = null, string? Password = null, string? AuthMode = "Integrated", string? CredentialMode = null);
public record ConnectDestinationRequest(string DestinationUrl, string? Email = null, string? Password = null, string? BearerToken = null);
public record DataSourceCredentialRequest(string? UserName = null, string? Password = null, string? CredentialRetrieval = null, string? ConnectString = null, bool? TrustServerCertificate = null);
public record ApproveRequest(string? UserId, string? UserName, string? Comment);
public record SelectReportsRequest(Guid[]? SelectedReportIds);
public record DeleteDestinationReportsRequest(Guid[]? ReportIds = null, string[]? Sections = null, bool DeleteAll = false, bool DeleteCategories = false);
public record BulkRemapDataSourcesRequest(
    Guid[]? DataSourceIds = null,
    string? FindServer = null,
    string? ReplaceServer = null,
    string? FindDatabase = null,
    string? ReplaceDatabase = null,
    string? RawFind = null,
    string? RawReplace = null,
    string? CredentialRetrieval = null,
    string? UserName = null,
    string? Password = null,
    bool? TrustServerCertificate = null,
    bool DryRun = false);
public record RebindReferenceRequest(string ReferenceType, string? OldReference, string NewReference);
