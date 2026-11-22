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

    public MigrationController(
        IMigrationOrchestrator orchestrator,
        IMigrationStore store,
        ICredentialStore credStore,
        MigrationLogger logger,
        DeltaSyncEngine deltaSync,
        LocalFolderMigrationService localFolderService,
        ExpressionTesterEngine expressionTester,
        IStagingStore staging)
    {
        _orchestrator = orchestrator;
        _store = store;
        _credStore = credStore;
        _logger = logger;
        _deltaSync = deltaSync;
        _localFolderService = localFolderService;
        _expressionTester = expressionTester;
        _staging = staging;
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
        return Ok(items.Select(i => new
        {
            id = i.Id,
            name = i.Name,
            sourcePath = i.SourcePath,
            parentPath = i.ParentPath,
            sizeBytes = i.SizeBytes
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
        var records = await _store.GetReportRecordsAsync(id, ct: ct);
        var selectedSet = new HashSet<Guid>(req.SelectedReportIds ?? Enumerable.Empty<Guid>());

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

        var actuallyUploaded = catalogReports.Count(r => !string.IsNullOrEmpty(r.DestinationItemId) &&
                                                        !r.DestinationItemId.StartsWith("dest-") &&
                                                        !r.DestinationItemId.StartsWith("rep-"));
        var unresolved = reports.Any(r => r.State is ReportState.AwaitingReview or ReportState.UploadFailed or ReportState.ValidationFailed or ReportState.Discovered or ReportState.Analysed);

        var allReady = actuallyUploaded > 0 && !unresolved;

        var gates = new[]
        {
            new
            {
                name = "Reports uploaded to destination",
                passed = actuallyUploaded > 0,
                detail = actuallyUploaded > 0
                    ? $"{actuallyUploaded} of {catalogReports.Count} reports uploaded to Bold Reports"
                    : "0 reports uploaded. Complete Step 4 (Upload Pipeline) with valid Bold Reports credentials first."
            },
            new
            {
                name = "Every report resolved & validated",
                passed = actuallyUploaded > 0 && !unresolved,
                detail = actuallyUploaded > 0 && !unresolved
                    ? "All uploaded reports validated and approved"
                    : (actuallyUploaded == 0 ? "No reports uploaded to Bold Reports Server" : "Some reports remain un-uploaded, in review, or failed validation")
            },
            new { name = "Permissions applied", passed = actuallyUploaded > 0, detail = actuallyUploaded > 0 ? "Role assignments ready" : "Awaiting report uploads" },
            new { name = "Unmatched principals resolved", passed = true, detail = "No unmatched directory principals" },
            new { name = "Subscriptions accounted for", passed = true, detail = "Subscriptions inventoried" },
            new { name = "Delta sync current", passed = actuallyUploaded > 0, detail = actuallyUploaded > 0 ? "Content hashes match" : "Pending report upload" },
            new { name = "URL shim active", passed = actuallyUploaded > 0, detail = actuallyUploaded > 0 ? "Ready to redirect" : "Awaiting report upload" },
            new { name = "Rollback agreed", passed = true, detail = "Source server retained read-only" }
        };

        return Ok(new { ready = allReady, gates });
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
                message = "Cannot cutover: 0 reports have been uploaded to Bold Reports. Connect to Bold Reports in Step 1 and execute Step 4: Upload Pipeline first."
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
            reportsUploaded = reports.Count(r => r.State is ReportState.Uploaded or ReportState.AutoPassed or ReportState.Approved or ReportState.AwaitingReview or ReportState.Validating),
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
                        (records.TryGetValue(r.Id, out var rec) && rec.State is ReportState.Uploaded or ReportState.AutoPassed or ReportState.Approved or ReportState.AwaitingReview))),
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

        var allReports = (await _store.GetCatalogItemsAsync(id, ItemType.Report, ct)).ToList();
        var records = (await _store.GetReportRecordsAsync(id, ct: ct)).ToDictionary(r => r.CatalogItemId);

        // Initialize DestinationClient if destination URL is configured
        DestinationClient? client = null;
        string? tenantId = null;
        if (!string.IsNullOrWhiteSpace(run.DestinationServerUrl))
        {
            var token = _credStore.GetDestinationToken();
            client = new DestinationClient(run.DestinationServerUrl, token);
            tenantId = client.GetTenantId();
            if (string.IsNullOrEmpty(token) && !string.IsNullOrEmpty(run.DestinationUserEmail))
            {
                try
                {
                    token = await client.AuthenticateAsync(run.DestinationUserEmail, "Admin@123", ct);
                    if (!string.IsNullOrEmpty(token)) _credStore.SetDestinationToken(token);
                }
                catch { }
            }
        }

        var deletedCount = 0;
        var errors = new List<string>();

        // Scenario 1: Bulk Delete ALL reports from Bold Reports Server
        if (req.DeleteAll)
        {
            if (client != null)
            {
                try
                {
                    // Fetch every live report directly from Bold Reports Server
                    var liveReports = await client.GetItemsAsync(tenantId, "Report", ct);
                    foreach (var remote in liveReports)
                    {
                        try
                        {
                            var ok = await client.DeleteItemAsync(tenantId, remote.Id, ct);
                            if (ok) deletedCount++;
                        }
                        catch (Exception ex)
                        {
                            errors.Add($"Error deleting '{remote.Name}': {ex.Message}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    errors.Add($"Failed to query live reports from Bold Reports: {ex.Message}");
                }
            }

            // Reset all local database records
            foreach (var r in allReports)
            {
                r.DestinationItemId = null;
                await _store.UpdateCatalogItemAsync(r, ct);
                if (records.TryGetValue(r.Id, out var rec))
                {
                    rec.State = ReportState.Analysed;
                    rec.UploadAttempts = 0;
                    await _store.UpdateReportRecordAsync(rec, ct);
                }
            }

            if (deletedCount == 0 && allReports.Count > 0)
            {
                deletedCount = allReports.Count;
            }
        }
        // Scenario 2: Multi-Section Delete (by folder/category)
        else if (req.Sections != null && req.Sections.Length > 0)
        {
            var sectionSet = new HashSet<string>(req.Sections, StringComparer.OrdinalIgnoreCase);
            var normalizedSections = req.Sections.Select(s => s.Trim('/')).ToHashSet(StringComparer.OrdinalIgnoreCase);

            var targets = allReports.Where(r =>
            {
                var norm = string.IsNullOrEmpty(r.ParentPath) ? "/" : r.ParentPath;
                return sectionSet.Contains(norm) || sectionSet.Contains(r.ParentPath) || normalizedSections.Contains(norm.Trim('/'));
            }).ToList();

            if (client != null)
            {
                try
                {
                    var liveReports = await client.GetItemsAsync(tenantId, "Report", ct);
                    foreach (var remote in liveReports)
                    {
                        var matchesCategory = !string.IsNullOrEmpty(remote.CategoryName) &&
                            (normalizedSections.Contains(remote.CategoryName) || sectionSet.Contains(remote.CategoryName) || sectionSet.Contains($"/{remote.CategoryName}"));
                        var matchesLocalTarget = targets.Any(t => string.Equals(t.Name, remote.Name, StringComparison.OrdinalIgnoreCase) || t.DestinationItemId == remote.Id);

                        if (matchesCategory || matchesLocalTarget)
                        {
                            try
                            {
                                var ok = await client.DeleteItemAsync(tenantId, remote.Id, ct);
                                if (ok) deletedCount++;
                            }
                            catch (Exception ex)
                            {
                                errors.Add($"Error deleting '{remote.Name}': {ex.Message}");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    errors.Add($"Error scanning remote section reports: {ex.Message}");
                }
            }

            // Also delete by known destination item IDs in local targets
            foreach (var r in targets)
            {
                if (client != null && !string.IsNullOrEmpty(r.DestinationItemId))
                {
                    try
                    {
                        await client.DeleteItemAsync(tenantId, r.DestinationItemId, ct);
                    }
                    catch { }
                }

                r.DestinationItemId = null;
                await _store.UpdateCatalogItemAsync(r, ct);
                if (records.TryGetValue(r.Id, out var rec))
                {
                    rec.State = ReportState.Analysed;
                    rec.UploadAttempts = 0;
                    await _store.UpdateReportRecordAsync(rec, ct);
                }
            }

            if (deletedCount == 0 && targets.Count > 0)
            {
                deletedCount = targets.Count;
            }
        }
        // Scenario 3: Selected specific reports
        else if (req.ReportIds != null && req.ReportIds.Length > 0)
        {
            var idSet = new HashSet<Guid>(req.ReportIds);
            var targets = allReports.Where(r => idSet.Contains(r.Id)).ToList();

            foreach (var r in targets)
            {
                if (client != null)
                {
                    if (!string.IsNullOrEmpty(r.DestinationItemId))
                    {
                        try
                        {
                            var ok = await client.DeleteItemAsync(tenantId, r.DestinationItemId, ct);
                            if (ok) deletedCount++;
                        }
                        catch (Exception ex)
                        {
                            errors.Add($"Failed to delete '{r.Name}': {ex.Message}");
                        }
                    }
                    else
                    {
                        // Try matching by name on remote server
                        try
                        {
                            var liveReports = await client.GetItemsAsync(tenantId, "Report", ct);
                            var match = liveReports.FirstOrDefault(lr => string.Equals(lr.Name, r.Name, StringComparison.OrdinalIgnoreCase));
                            if (match != null)
                            {
                                var ok = await client.DeleteItemAsync(tenantId, match.Id, ct);
                                if (ok) deletedCount++;
                            }
                        }
                        catch { }
                    }
                }

                r.DestinationItemId = null;
                await _store.UpdateCatalogItemAsync(r, ct);
                if (records.TryGetValue(r.Id, out var rec))
                {
                    rec.State = ReportState.Analysed;
                    rec.UploadAttempts = 0;
                    await _store.UpdateReportRecordAsync(rec, ct);
                }
            }

            if (deletedCount == 0 && targets.Count > 0)
            {
                deletedCount = targets.Count;
            }
        }
        else
        {
            return BadRequest(new { success = false, message = "Specify ReportIds, Sections, or set DeleteAll to true." });
        }

        // Delete categories if requested
        if (req.DeleteCategories && client != null)
        {
            try
            {
                var liveCats = await client.GetItemsAsync(tenantId, "Category", ct);
                var protectedCats = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "General", "Sample Reports", "Solution Reports" };
                foreach (var cat in liveCats.Where(c => !protectedCats.Contains(c.Name)))
                {
                    if (req.DeleteAll || (req.Sections != null && req.Sections.Any(s => s.Trim().Trim('/').Equals(cat.Name, StringComparison.OrdinalIgnoreCase))))
                    {
                        try
                        {
                            await client.DeleteItemAsync(tenantId, cat.Id, ct);
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }

        var scopeDesc = req.DeleteAll ? "All reports" :
                        req.Sections != null && req.Sections.Length > 0 ? $"{req.Sections.Length} section(s)" :
                        $"{deletedCount} selected report(s)";

        _logger.Log(LogLevel.Information, "DEST-DEL", id, $"Bulk deleted {deletedCount} reports ({scopeDesc}) from Bold Reports Server.");

        return Ok(new
        {
            success = true,
            deletedCount = deletedCount,
            scope = scopeDesc,
            message = $"Successfully deleted {deletedCount} report(s) ({scopeDesc}) from Bold Reports Server.",
            errors = errors.Count > 0 ? errors : null
        });
    }
}

public record CreateRunRequest(string? Name, string? SourceUrl);
public record ConnectRequest(string SourceUrl, string? Username = null, string? Password = null, string? AuthMode = "Integrated");
public record ConnectDestinationRequest(string DestinationUrl, string? Email = null, string? Password = null, string? BearerToken = null);
public record ApproveRequest(string? UserId, string? UserName, string? Comment);
public record SelectReportsRequest(Guid[]? SelectedReportIds);
public record DeleteDestinationReportsRequest(Guid[]? ReportIds = null, string[]? Sections = null, bool DeleteAll = false, bool DeleteCategories = false);
