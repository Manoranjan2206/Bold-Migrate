using System.Net;
using BoldMigrate.Core;
using BoldMigrate.Core.Dto;
using BoldMigrate.Core.Enums;
using BoldMigrate.Core.Interfaces;
using BoldMigrate.Core.Models;
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

    public MigrationController(
        IMigrationOrchestrator orchestrator,
        IMigrationStore store,
        ICredentialStore credStore,
        MigrationLogger logger,
        DeltaSyncEngine deltaSync,
        LocalFolderMigrationService localFolderService,
        ExpressionTesterEngine expressionTester,
        IStagingStore staging,
        BoldMigrate.Core.Configuration.MigrationSettings settings)
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
    }

    [HttpPost("runs")]
    public async Task<IActionResult> CreateOrGetRun([FromBody] CreateRunRequest req, CancellationToken ct)
    {
        var run = await _orchestrator.CreateOrGetActiveRunAsync(req.Name ?? "SSRS Migration", req.SourceUrl ?? "http://localhost/ReportServer", ct);
        return Ok(run);
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
        var (adapter, api) = await SourceAdapterFactory.CreateAndProbeAsync(resolvedUrl, cred, run.SourceApi, ct);
        var probe = await adapter.ProbeAsync(resolvedUrl, cred, ct);

        // If explicit credentials were provided and failed with 401 Unauthorized,
        // test whether Integrated Windows Authentication succeeds on this endpoint.
        if (!probe.Success && probe.ErrorCode == ErrorCatalogue.Src002 && cred != null)
        {
            try
            {
                var (intAdapter, intApi) = await SourceAdapterFactory.CreateAndProbeAsync(resolvedUrl, null, run.SourceApi, ct);
                var intProbe = await intAdapter.ProbeAsync(resolvedUrl, null, ct);
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
            run.SourceVersion = probe.SourceVersion;
            run.Status = RunStatus.Connected;
            await _store.UpdateRunAsync(run, ct);

            _logger.Log(LogLevel.Information, "CONN-OK", id, $"Connected. {probe.SourceVersion}, using {api}.");
            return Ok(new { success = true, version = probe.SourceVersion, api = api.ToString() });
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
            var destClient = new DestinationClient(req.DestinationUrl, req.BearerToken);
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

                _credStore.SetDestinationToken(token);
                _logger.Log(LogLevel.Information, "DEST-OK", id, $"Connected to Bold Reports Server ({req.DestinationUrl})");
                return Ok(new { success = true, message = "Authenticated with Bold Reports Server" });
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
        _ = Task.Run(async () =>
        {
            try
            {
                await _orchestrator.StartFetchAsync(id, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.Log(LogLevel.Error, "DISC-ERROR", id, $"Discovery failed: {ex.Message}");
            }
        });
        return Accepted(new { message = "Discovery started" });
    }

    [HttpPost("runs/{id:guid}/check")]
    public async Task<IActionResult> CheckEstate(Guid id, CancellationToken ct)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await _orchestrator.StartCheckAsync(id, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.Log(LogLevel.Error, "XFRM-ERROR", id, $"Check failed: {ex.Message}");
            }
        });
        return Accepted(new { message = "Transform check started" });
    }

    [HttpPost("runs/{id:guid}/upload")]
    public async Task<IActionResult> StartUpload(Guid id, [FromQuery] string? action, CancellationToken ct)
    {
        if (action == "pause")
        {
            _orchestrator.PauseRun(id);
            return Ok(new { status = "paused" });
        }
        if (action == "resume")
        {
            _orchestrator.ResumeRun(id);
            return Ok(new { status = "resumed" });
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await _orchestrator.StartUploadAsync(id, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.Log(LogLevel.Error, "UPLD-ERROR", id, $"Upload failed: {ex.Message}");
            }
        });
        return Accepted(new { message = "Upload started" });
    }

    [HttpPost("runs/{id:guid}/validate")]
    public async Task<IActionResult> StartValidation(Guid id, CancellationToken ct)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await _orchestrator.StartValidationAsync(id, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.Log(LogLevel.Error, "VALD-ERROR", id, $"Validation failed: {ex.Message}");
            }
        });
        return Accepted(new { message = "Validation started" });
    }

    [HttpPost("runs/{id:guid}/retry")]
    public async Task<IActionResult> RetryFailed(Guid id, CancellationToken ct)
    {
        _ = Task.Run(async () => await _orchestrator.RetryFailedItemsAsync(id, CancellationToken.None));
        return Accepted(new { message = "Retrying failed items" });
    }

    [HttpPost("runs/{id:guid}/abandon")]
    public async Task<IActionResult> Abandon(Guid id, CancellationToken ct)
    {
        await _orchestrator.AbandonRunAsync(id, ct);
        return Ok(new { status = "abandoned" });
    }

    [HttpGet("runs/{id:guid}/estate")]
    public async Task<IActionResult> GetEstate(Guid id, CancellationToken ct)
    {
        var items = await _store.GetCatalogItemsAsync(id, ct: ct);
        return Ok(new
        {
            folders = items.Count(i => i.ItemType == ItemType.Folder),
            dataSources = items.Count(i => i.ItemType == ItemType.DataSource),
            datasets = items.Count(i => i.ItemType == ItemType.DataSet),
            reports = items.Count(i => i.ItemType == ItemType.Report),
            items = items.Take(100)
        });
    }

    [HttpGet("runs/{id:guid}/datasources")]
    public async Task<IActionResult> GetDataSources(Guid id, CancellationToken ct)
    {
        var items = await _store.GetCatalogItemsAsync(id, ItemType.DataSource, ct);
        return Ok(items.Select(i =>
        {
            // Only the user name is returned; the stored password never leaves the server.
            var cred = _credStore.GetDataSourceCredential(i.Id.ToString());
            return new
            {
                id = i.Id,
                name = i.Name,
                sourcePath = i.SourcePath,
                parentPath = i.ParentPath,
                sizeBytes = i.SizeBytes,
                hasCredentials = cred != null,
                userName = cred == null ? null : string.IsNullOrEmpty(cred.Domain) ? cred.UserName : $"{cred.Domain}\\{cred.UserName}"
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

            if (selectedSet.Contains(r.CatalogItemId))
            {
                if (r.State == ReportState.Excluded)
                {
                    r.State = ReportState.Analysed;
                    await _store.UpdateReportRecordAsync(r, ct);
                }
            }
            else
            {
                if (r.State != ReportState.Excluded)
                {
                    r.State = ReportState.Excluded;
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
        if (item == null) return NotFound(new { message = "Report not found" });

        var record = await _store.GetReportRecordAsync(reportId, ct);

        string originalRdl = "";
        string transformedRdl = "";

        string? resolvedPath = null;
        if (!string.IsNullOrEmpty(item.StagedFilePath))
        {
            if (System.IO.File.Exists(item.StagedFilePath))
            {
                resolvedPath = item.StagedFilePath;
            }
            else
            {
                var candidate = Path.Combine("C:", item.StagedFilePath.TrimStart('/', '\\'));
                if (System.IO.File.Exists(candidate))
                {
                    resolvedPath = candidate;
                }
                else
                {
                    // Look in C:\migrations
                    var filename = Path.GetFileName(item.StagedFilePath);
                    if (Directory.Exists("C:\\migrations"))
                    {
                        var matches = Directory.GetFiles("C:\\migrations", filename, SearchOption.AllDirectories);
                        if (matches.Length > 0)
                        {
                            resolvedPath = matches[0];
                        }
                    }
                }
            }
        }

        if (resolvedPath != null && System.IO.File.Exists(resolvedPath))
        {
            try
            {
                var bytes = await System.IO.File.ReadAllBytesAsync(resolvedPath, ct);
                originalRdl = System.Text.Encoding.UTF8.GetString(bytes);
                transformedRdl = originalRdl;

                // If report was auto-fixed, show the updated namespace or expression changes
                if (record?.Bucket == ReportBucket.AutoFixed && !string.IsNullOrEmpty(originalRdl))
                {
                    transformedRdl = originalRdl
                        .Replace("http://schemas.microsoft.com/sqlserver/reporting/2010/01/reportdefinition", "http://schemas.microsoft.com/sqlserver/reporting/2016/01/reportdefinition")
                        .Replace("http://schemas.microsoft.com/sqlserver/reporting/2008/01/reportdefinition", "http://schemas.microsoft.com/sqlserver/reporting/2016/01/reportdefinition");
                }
            }
            catch { }
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
            parameterSetHash = latest.ParameterSetHash,
            runUtc = latest.RunUtc
        });
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

        record.State = ReportState.Approved;
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

        record.State = ReportState.Flagged;
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
            var latest = results.FirstOrDefault();
            if (latest != null && latest.DataAccuracy >= minAccuracy)
            {
                r.State = ReportState.Approved;
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
        var run = await _store.GetRunAsync(id, ct) ?? throw new KeyNotFoundException("Run not found.");
        var source = SourceAdapterFactory.Create(run.SourceServerUrl, run.SourceApi);
        var dest = new DestinationClient(run.SourceServerUrl);
        var res = await _deltaSync.RunDeltaSyncAsync(id, source, dest, ct);
        return Ok(res);
    }

    [HttpGet("runs/{id:guid}/cutover")]
    public async Task<IActionResult> GetCutoverGates(Guid id, CancellationToken ct)
    {
        var reports = await _store.GetReportRecordsAsync(id, ct: ct);
        var catalogReports = await _store.GetCatalogItemsAsync(id, ItemType.Report, ct);

        var uploadedItems = catalogReports.Where(r => !string.IsNullOrEmpty(r.DestinationItemId) &&
                                                      !r.DestinationItemId.StartsWith("dest-") &&
                                                      !r.DestinationItemId.StartsWith("rep-")).ToList();
        var actuallyUploaded = uploadedItems.Count;

        var selected = reports.Where(r => r.State != ReportState.Excluded).ToList();
        var awaitingReview = selected.Count(r => r.State == ReportState.AwaitingReview);
        var uploadFailed = selected.Count(r => r.State == ReportState.UploadFailed);
        var validationFailed = selected.Count(r => r.State == ReportState.ValidationFailed);
        var notStarted = selected.Count(r => r.State is ReportState.Discovered or ReportState.Analysed or ReportState.Queued);

        // A report counts as validated only when a measured validation result exists for it.
        var validatedCount = 0;
        foreach (var item in uploadedItems)
        {
            var results = await _store.GetValidationResultsAsync(item.Id, ct);
            if (results.Count > 0) validatedCount++;
        }

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
        var actuallyUploaded = catalogReports.Count(r => !string.IsNullOrEmpty(r.DestinationItemId) &&
                                                        !r.DestinationItemId.StartsWith("dest-") &&
                                                        !r.DestinationItemId.StartsWith("rep-"));
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
    public IActionResult GetLiveLog()
    {
        return Ok(_logger.GetLiveLog());
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
    /// Step 1: stores the account a shared data source connects with (in memory only). The
    /// local viewer/designer use it; without it they prompt for credentials at run time.
    /// </summary>
    [HttpPut("runs/{id:guid}/datasources/{dataSourceId:guid}/credentials")]
    public async Task<IActionResult> SetDataSourceCredentials(Guid id, Guid dataSourceId, [FromBody] DataSourceCredentialRequest req, CancellationToken ct)
    {
        var item = await _store.GetCatalogItemAsync(dataSourceId, ct);
        if (item == null || item.RunId != id || item.ItemType != ItemType.DataSource) return NotFound();

        if (string.IsNullOrWhiteSpace(req.UserName))
        {
            _credStore.RemoveDataSourceCredential(dataSourceId.ToString());
            return Ok(new { success = true, hasCredentials = false });
        }

        var user = req.UserName.Trim();
        var cred = user.Contains('\\')
            ? new NetworkCredential(user.Split('\\', 2)[1], req.Password ?? "", user.Split('\\', 2)[0])
            : new NetworkCredential(user, req.Password ?? "");
        _credStore.SetDataSourceCredential(dataSourceId.ToString(), cred);

        _logger.Log(LogLevel.Information, "CONN-DS-CRED", id, $"Credentials set for data source '{item.Name}'.", itemPath: item.SourcePath);
        return Ok(new { success = true, hasCredentials = true });
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
        !string.IsNullOrEmpty(id) && !id.StartsWith("dest-") && !id.StartsWith("rep-");

    [HttpGet("runs/{id:guid}/designer-config")]
    public async Task<IActionResult> GetDesignerConfig(Guid id, CancellationToken ct)
    {
        var run = await _store.GetRunAsync(id, ct);
        if (run == null) return NotFound();

        var destUrl = !string.IsNullOrWhiteSpace(run.DestinationServerUrl) ? run.DestinationServerUrl : "http://localhost:8090/reporting/site/site1";
        var client = new DestinationClient(destUrl);
        var tenantId = client.GetTenantId();
        var token = _credStore.GetDestinationToken();
        if (string.IsNullOrEmpty(token) && !string.IsNullOrEmpty(run.DestinationUserEmail))
        {
            try
            {
                token = await client.AuthenticateAsync(run.DestinationUserEmail, "Admin@123", ct);
                if (!string.IsNullOrEmpty(token)) _credStore.SetDestinationToken(token);
            }
            catch { }
        }

        var origin = "http://localhost:8090";
        if (Uri.TryCreate(destUrl, UriKind.Absolute, out var uri))
        {
            origin = $"{uri.Scheme}://{uri.Authority}".TrimEnd('/');
        }

        return Ok(new
        {
            serviceUrl = $"{origin}/reporting/reportservice/api/Designer",
            cloudServiceUrl = "https://service.boldreports.com/api/Designer",
            reportServerUrl = $"{origin}/reporting/api/site/{tenantId}",
            serviceAuthorizationToken = !string.IsNullOrEmpty(token) ? $"bearer {token}" : "",
            tenantId,
            serverUrl = origin
        });
    }

    [HttpGet("runs/{id:guid}/destination/sections")]
    public async Task<IActionResult> GetSections(Guid id, CancellationToken ct)
    {
        var run = await _store.GetRunAsync(id, ct);
        if (run == null) return NotFound();

        var reports = await _store.GetCatalogItemsAsync(id, ItemType.Report, ct);
        var records = (await _store.GetReportRecordsAsync(id, ct: ct)).ToDictionary(r => r.CatalogItemId);

        // Check live items on Bold Reports Server if connected
        List<DestinationItemInfo>? liveRemoteReports = null;
        if (!string.IsNullOrWhiteSpace(run.DestinationServerUrl))
        {
            try
            {
                var token = _credStore.GetDestinationToken();
                var client = new DestinationClient(run.DestinationServerUrl, token);
                var tenantId = client.GetTenantId();
                if (string.IsNullOrEmpty(token) && !string.IsNullOrEmpty(run.DestinationUserEmail))
                {
                    try { token = await client.AuthenticateAsync(run.DestinationUserEmail, "Admin@123", ct); } catch { }
                }
                liveRemoteReports = await client.GetItemsAsync(tenantId, "Report", ct);
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

        var client = new DestinationClient(run.DestinationServerUrl, token);
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
                rec.State = ReportState.Analysed;
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
}

public record CreateRunRequest(string? Name, string? SourceUrl);
public record ConnectRequest(string SourceUrl, string? Username = null, string? Password = null, string? AuthMode = "Integrated");
public record ConnectDestinationRequest(string DestinationUrl, string? Email = null, string? Password = null, string? BearerToken = null);
public record DataSourceCredentialRequest(string? UserName = null, string? Password = null);
public record ApproveRequest(string? UserId, string? UserName, string? Comment);
public record SelectReportsRequest(Guid[]? SelectedReportIds);
public record DeleteDestinationReportsRequest(Guid[]? ReportIds = null, string[]? Sections = null, bool DeleteAll = false, bool DeleteCategories = false);
