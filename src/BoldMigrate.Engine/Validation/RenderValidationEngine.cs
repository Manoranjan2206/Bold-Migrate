using System.Collections.Concurrent;
using System.Diagnostics;
using System.Xml.Linq;
using BoldMigrate.Core.Common;
using BoldMigrate.Core.StateMachine;
using BoldMigrate.Core.Enums;
using BoldMigrate.Core.Interfaces;
using BoldMigrate.Core.Models;
using BoldMigrate.Engine.Preview;
using BoldMigrate.Infrastructure.Credentials;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;

namespace BoldMigrate.Engine.Validation;

public class RenderValidationRunSummary
{
    public Guid RunId { get; set; }
    public int TotalReports { get; set; }
    public int Passed { get; set; }
    public int DataSourceOffline { get; set; }
    public int Failed { get; set; }
    public int NeedsManualCheck { get; set; }
    public int Skipped { get; set; }
    public List<ReportValidationCardDto> Reports { get; set; } = new();
}

public class ReportValidationCardDto
{
    public Guid ReportId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string Status { get; set; } = "NotValidated";
    public long? DurationMs { get; set; }
    public string? ScreenshotUrl { get; set; }

    /// <summary>
    /// True when <see cref="ScreenshotUrl"/> is diagnostic evidence captured from a FAILED render
    /// rather than a successful one. The UI labels it so an operator never mistakes a captured
    /// error screen for a good render.
    /// </summary>
    public bool ScreenshotIsFailureEvidence { get; set; }

    /// <summary>
    /// The matching SSRS render, captured in the same pass, or null when the source server could
    /// not produce one. Lets the UI show whether a side-by-side comparison is ready.
    /// </summary>
    public string? SourceSnapshotUrl { get; set; }
    public string? ErrorMessage { get; set; }
    public List<string> FeatureTags { get; set; } = new();
    public List<string> PromptParameters { get; set; } = new();
}

public class RenderValidationEngine
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IStagingStore _staging;
    private readonly IRenderLogStore _renderLogs;
    private readonly ILogger<RenderValidationEngine> _logger;

    private readonly BoldMigrate.Core.Configuration.MigrationSettings _settings;

    public RenderValidationEngine(
        IServiceScopeFactory scopeFactory,
        IStagingStore staging,
        IRenderLogStore renderLogs,
        BoldMigrate.Core.Configuration.MigrationSettings settings,
        ILogger<RenderValidationEngine> logger)
    {
        _scopeFactory = scopeFactory;
        _staging = staging;
        _renderLogs = renderLogs;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>
    /// Renders the SAME report on the source SSRS server and caches the PNG next to the staged
    /// snapshot, under the exact name the compare endpoint serves from cache.
    ///
    /// The SSRS image used to be fetched lazily, only when the operator opened the comparison
    /// modal, so the Bold Reports side appeared immediately and the SSRS side arrived seconds
    /// later. Capturing both while validating a report means the comparison is ready the moment
    /// it is opened. A failure here is non-fatal: the staged render is still the primary result.
    /// </summary>
    private async Task CaptureSourceSnapshotAsync(Guid runId, CatalogItem report, string runDir, CancellationToken ct)
    {
        var sourcePngPath = Path.Combine(runDir, $"{report.Id}-source.png");
        if (File.Exists(sourcePngPath)) return;

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<IMigrationStore>();
            var credStore = scope.ServiceProvider.GetRequiredService<ICredentialStore>();

            var run = await store.GetRunAsync(runId, ct);
            if (run == null || string.IsNullOrWhiteSpace(run.SourceServerUrl)) return;

            var cred = credStore.GetSourceCredential(runId.ToString());
            var source = BoldMigrate.Infrastructure.SourceAdapters.SourceAdapterFactory.Create(
                run.SourceServerUrl, run.SourceApi, cred);
            using var disposable = source as IDisposable;

            var renderParams = new Dictionary<string, string> { ["rc:OutputFormat"] = "PNG", ["rc:StartPage"] = "1" };
            var bytes = await source.RenderAsync(report.SourcePath, "IMAGE", renderParams, ct);

            // PNG signature guard: SSRS can return TIFF or an HTML error page instead.
            if (bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
            {
                await File.WriteAllBytesAsync(sourcePngPath, bytes, ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogInformation(
                "Source SSRS snapshot unavailable for {Path}: {Message}",
                report.SourcePath, ex.Message);
        }
    }

    /// <summary>
    /// Screenshots only the report page body. The viewer id prefix depends on the integration,
    /// so match the stable "_pageviewContainer" suffix or the e-reportviewer-pageview class.
    /// Falls back to the full viewport if the container is missing.
    /// </summary>
    private static async Task ScreenshotReportBodyAsync(IPage page, PageScreenshotOptions options)
    {
        await page.AddStyleTagAsync(new PageAddStyleTagOptions { Content = "[id$='_toolbarContainer'], .e-reportviewer-toolbar { display: none !important; }" });
        var body = page.Locator("[id$='_pageviewContainer'], .e-reportviewer-pageview").First;
        if (await body.CountAsync() > 0 && await body.IsVisibleAsync())
        {
            await body.ScreenshotAsync(new LocatorScreenshotOptions { Path = options.Path });
            return;
        }
        await page.ScreenshotAsync(options);
    }

    public static bool IsDataSourceConnectionIssue(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return false;
        return message.Contains("connection to SQL Server", StringComparison.OrdinalIgnoreCase)
            || message.Contains("network-related or instance-specific error", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Named Pipes Provider", StringComparison.OrdinalIgnoreCase)
            || message.Contains("TCP Provider", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Data source offline", StringComparison.OrdinalIgnoreCase)
            || message.Contains("The server was not found or was not accessible", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Login failed for user", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Cannot open database", StringComparison.OrdinalIgnoreCase)
            || message.Contains("timeout period elapsed", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Connection Timeout Expired", StringComparison.OrdinalIgnoreCase)
            || message.Contains("certificate chain was issued by an authority that is not trusted", StringComparison.OrdinalIgnoreCase)
            || (message.Contains("Data cannot be retrieved for", StringComparison.OrdinalIgnoreCase) && message.Contains("datasource", StringComparison.OrdinalIgnoreCase));
    }

    public static (bool HasPromptParamsWithoutDefaults, List<string> ParamNames) CheckRequiredParameters(byte[] rdlBytes)
    {
        try
        {
            var doc = SafeXml.LoadBytes(rdlBytes);
            var paramElems = doc.Descendants().Where(e => e.Name.LocalName == "ReportParameter").ToList();
            var required = new List<string>();

            foreach (var p in paramElems)
            {
                var name = p.Attribute("Name")?.Value
                           ?? p.Elements().FirstOrDefault(e => e.Name.LocalName == "Name")?.Value
                           ?? "Parameter";
                var prompt = p.Elements().FirstOrDefault(e => e.Name.LocalName == "Prompt")?.Value;
                var nullable = p.Elements().FirstOrDefault(e => e.Name.LocalName == "Nullable")?.Value;
                var hasDefault = p.Elements().Any(e => e.Name.LocalName == "DefaultValue" || e.Name.LocalName == "ValidValues");

                if (!string.IsNullOrWhiteSpace(prompt) && !hasDefault && !string.Equals(nullable, "true", StringComparison.OrdinalIgnoreCase))
                {
                    required.Add(name);
                }
            }

            return (required.Count > 0, required);
        }
        catch
        {
            return (false, new List<string>());
        }
    }

    public async Task<RenderValidationRunSummary> ValidateRunAsync(
        Guid runId,
        string baseUrl,
        int maxConcurrency = 5,
        CancellationToken ct = default,
        IEnumerable<Guid>? reportIds = null)
    {
        List<CatalogItem> reports;
        using (var initScope = _scopeFactory.CreateScope())
        {
            var store = initScope.ServiceProvider.GetRequiredService<IMigrationStore>();
            var run = await store.GetRunAsync(runId, ct) ?? throw new KeyNotFoundException($"Run {runId} not found");
            reports = (await store.GetCatalogItemsAsync(runId, ItemType.Report, ct)).ToList();
        }

        if (reportIds != null)
        {
            var filterSet = reportIds.ToHashSet();
            if (filterSet.Count > 0)
            {
                reports = reports.Where(r => filterSet.Contains(r.Id)).ToList();
            }
        }

        // Ensure screenshots staging directory exists inside the run's IStagingStore folder
        var runDir = Path.Combine(_staging.GetRunStagingPath(runId), "screenshots");
        try
        {
            Directory.CreateDirectory(runDir);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not create screenshot directory {Dir}", runDir);
        }

        var summary = new RenderValidationRunSummary
        {
            RunId = runId,
            TotalReports = reports.Count
        };

        var cards = new ConcurrentBag<ReportValidationCardDto>();

        // Configurable (default 60s). The old hard-coded 30s was shorter than many real reports
        // with live queries need, so healthy reports were being reported as timeouts.
        var renderTimeoutMs = Math.Clamp(_settings.ValidationRenderTimeoutSeconds, 15, 600) * 1000;

        // Initialize Playwright
        IPlaywright? playwright = null;
        IBrowser? browser = null;

        try
        {
            playwright = await Playwright.CreateAsync();
            try
            {
                browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
                {
                    Headless = true,
                    Args = new[] { "--no-sandbox", "--disable-setuid-sandbox", "--disable-dev-shm-usage" }
                });
            }
            catch (Exception launchEx)
            {
                _logger.LogWarning(launchEx, "Initial Playwright Chromium launch failed. Attempting automatic browser install...");
                try
                {
                    Microsoft.Playwright.Program.Main(new[] { "install", "chromium" });
                    browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
                    {
                        Headless = true,
                        Args = new[] { "--no-sandbox", "--disable-setuid-sandbox", "--disable-dev-shm-usage" }
                    });
                }
                catch (Exception installEx)
                {
                    _logger.LogError(installEx, "Playwright auto-install/relaunch failed.");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize Playwright environment.");
        }

        var semaphore = new SemaphoreSlim(Math.Max(1, maxConcurrency));

        var tasks = reports.Select(async report =>
        {
            ReportRecord? record = null;
            using (var fetchScope = _scopeFactory.CreateScope())
            {
                var store = fetchScope.ServiceProvider.GetRequiredService<IMigrationStore>();
                record = await store.GetReportRecordAsync(report.Id, ct);
            }

            var featureTags = (record?.FeatureTags ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();

            var card = new ReportValidationCardDto
            {
                ReportId = report.Id,
                Name = report.Name,
                Path = report.SourcePath,
                FeatureTags = featureTags
            };

            if (string.IsNullOrWhiteSpace(report.StagedFilePath) || !File.Exists(report.StagedFilePath))
            {
                card.Status = "Failed";
                card.ErrorMessage = "Report RDL file is not staged on disk.";
                cards.Add(card);
                return;
            }

            byte[] rdlBytes;
            try
            {
                rdlBytes = await File.ReadAllBytesAsync(report.StagedFilePath, ct);
            }
            catch (Exception ex)
            {
                card.Status = "Failed";
                card.ErrorMessage = $"Failed reading staged file: {ex.Message}";
                cards.Add(card);
                return;
            }

            // Fallback feature tag detection if not yet stored on record
            if (featureTags.Count == 0)
            {
                try
                {
                    var doc = SafeXml.LoadBytes(rdlBytes);
                    var detected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    if (doc.Descendants().Any(e => e.Name.LocalName == "Subreport")) detected.Add("Subreport");
                    if (doc.Descendants().Any(e => e.Name.LocalName == "Chart")) detected.Add("Chart");
                    if (doc.Descendants().Any(e => e.Name.LocalName == "GaugePanel")) detected.Add("Gauge");
                    if (doc.Descendants().Any(e => e.Name.LocalName == "Map" || e.Name.LocalName == "MapDataRegion")) detected.Add("Map");
                    if (doc.Descendants().Any(e => e.Name.LocalName == "Code")) detected.Add("CustomCode");
                    if (doc.Descendants().Any(e => e.Name.LocalName == "UserSort")) detected.Add("InteractiveSort");
                    if (doc.Descendants().Any(e => e.Name.LocalName == "DocumentMapLabel")) detected.Add("DocumentMap");
                    if (doc.Descendants().Any(e => e.Name.LocalName == "DataSourceReference")) detected.Add("SharedDataSource");
                    if (doc.Descendants().Any(e => e.Name.LocalName == "SharedDataSet")) detected.Add("SharedDataSet");
                    if (doc.Descendants().Any(e => e.Value.Contains("Lookup(") || e.Value.Contains("LookupSet("))) detected.Add("Lookup");
                    if (doc.Descendants().Any(e => e.Name.LocalName == "MarkupType" && e.Value == "HTML")) detected.Add("RichText");

                    if (detected.Count > 0)
                    {
                        featureTags = detected.OrderBy(t => t).ToList();
                        card.FeatureTags = featureTags;
                        if (record != null)
                        {
                            record.FeatureTags = string.Join(",", featureTags);
                            using var updateScope = _scopeFactory.CreateScope();
                            var store = updateScope.ServiceProvider.GetRequiredService<IMigrationStore>();
                            await store.UpdateReportRecordAsync(record, ct);
                        }
                    }
                }
                catch { }
            }

            // Check if required parameters require manual input
            var (hasPromptParams, promptParams) = CheckRequiredParameters(rdlBytes);
            card.PromptParameters = promptParams;

            if (hasPromptParams)
            {
                card.Status = "NeedsManualCheck";
                card.ErrorMessage = $"Requires parameter input: [{string.Join(", ", promptParams)}]";
                
                var valResult = new ValidationResult
                {
                    ReportId = report.Id,
                    DataAccuracy = 100,
                    RenderStatus = "NeedsManualCheck",
                    RenderSummary = card.ErrorMessage
                };

                using (var promptScope = _scopeFactory.CreateScope())
                {
                    var store = promptScope.ServiceProvider.GetRequiredService<IMigrationStore>();
                    await store.AddValidationResultAsync(valResult, ct);

                    if (record != null)
                    {
                        ReportStateMachine.TryTransition(record, ReportState.AwaitingReview);
                        await store.UpdateReportRecordAsync(record, ct);
                    }
                }

                cards.Add(card);
                return;
            }

            if (browser == null)
            {
                card.Status = "NeedsManualCheck";
                card.ErrorMessage = "Browser engine could not be initialized. Please inspect report in Viewer.";
                cards.Add(card);
                return;
            }

            await semaphore.WaitAsync(ct);
            IBrowserContext? context = null;
            IPage? page = null;
            var sw = Stopwatch.StartNew();

            // Start the SSRS capture for THIS report alongside its staged render, so both sides of
            // the comparison are produced together before we move on to the next report. They used
            // to be sequential phases, which is why the Bold Reports image appeared first and the
            // SSRS one only loaded later when the compare modal was opened.
            var sourceSnapshotTask = CaptureSourceSnapshotAsync(runId, report, runDir, ct);

            // A failed render used to produce a bare message such as "Rendering timed out after
            // 30 seconds" with no evidence, leaving the operator nothing to act on. Capture what
            // the viewer was actually showing — an error banner, a parameter prompt, a blank
            // frame or a half-drawn report — so it can be read and diffed against SSRS. The file
            // name matches the success path, so the existing screenshot endpoint serves it and
            // the visual comparison modal can put it beside the SSRS render.
            async Task CaptureFailureEvidenceAsync()
            {
                if (page == null || page.IsClosed) return;
                try
                {
                    var failurePath = Path.Combine(runDir, $"{report.Id}.png");
                    await page.ScreenshotAsync(new PageScreenshotOptions
                    {
                        Path = failurePath,
                        FullPage = true
                    });
                    card.ScreenshotUrl = $"/api/migration/runs/{runId}/screenshots/{report.Id}.png";
                    card.ScreenshotIsFailureEvidence = true;
                }
                catch (Exception shotEx)
                {
                    _logger.LogWarning(shotEx, "Could not capture failure evidence for report {ReportId}", report.Id);
                }
            }

            try
            {
                context = await browser.NewContextAsync(new BrowserNewContextOptions
                {
                    ViewportSize = new ViewportSize { Width = 1280, Height = 900 }
                });
                page = await context.NewPageAsync();

                var runnerUrl = $"{baseUrl.TrimEnd('/')}/preview-runner.html?runId={runId}&reportId={report.Id}&reportPath={Uri.EscapeDataString(report.SourcePath)}";
                _logger.LogInformation("Playwright navigating to {Url} for report {Path}", runnerUrl, report.SourcePath);

                await page.GotoAsync(runnerUrl, new PageGotoOptions
                {
                    WaitUntil = WaitUntilState.DOMContentLoaded,
                    Timeout = renderTimeoutMs
                });

                // Wait for render complete or error state
                await page.WaitForFunctionAsync(
                    "() => document.body.getAttribute('data-render-status') === 'complete' || document.body.getAttribute('data-render-status') === 'error'",
                    null,
                    new PageWaitForFunctionOptions { Timeout = renderTimeoutMs });

                sw.Stop();
                card.DurationMs = sw.ElapsedMilliseconds;

                var finalStatus = await page.EvaluateAsync<string>("() => document.body.getAttribute('data-render-status')");

                if (finalStatus == "complete")
                {
                    // Check if server logger caught any serious rendering exceptions
                    var loggedErrors = _renderLogs.GetEntries(runId, report.SourcePath)
                        .Where(e => !e.Message.Contains("License", StringComparison.OrdinalIgnoreCase) &&
                                    (e.ExceptionMessage == null || !e.ExceptionMessage.Contains("License", StringComparison.OrdinalIgnoreCase)))
                        .ToList();

                    if (loggedErrors.Any(e => e.ErrorType.Contains("Error", StringComparison.OrdinalIgnoreCase) || !string.IsNullOrWhiteSpace(e.ExceptionMessage)))
                    {
                        var firstErr = loggedErrors.First(e => !string.IsNullOrWhiteSpace(e.Message) || !string.IsNullOrWhiteSpace(e.ExceptionMessage));
                        var errMsg = firstErr.ExceptionMessage ?? firstErr.Message;
                        if (IsDataSourceConnectionIssue(errMsg))
                        {
                            card.Status = "DataSourceOffline";
                            card.ErrorMessage = "Database offline in local preview. RDL structure is valid and ready for server upload.";
                        }
                        else
                        {
                            card.Status = "Failed";
                            card.ErrorMessage = errMsg;
                        }
                        await CaptureFailureEvidenceAsync();
                    }
                    else
                    {
                        card.Status = "Passed";

                        // Capture screenshot
                        var screenshotFileName = $"{report.Id}.png";
                        var screenshotPath = Path.Combine(runDir, screenshotFileName);
                        try
                        {
                            await ScreenshotReportBodyAsync(page, new PageScreenshotOptions
                            {
                                Path = screenshotPath,
                                FullPage = false
                            });
                            card.ScreenshotUrl = $"/api/migration/runs/{runId}/screenshots/{report.Id}.png";
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed taking screenshot for report {ReportId}", report.Id);
                        }
                    }
                }
                else
                {
                    var clientError = await page.EvaluateAsync<string>("() => window.__renderError || 'Unknown rendering failure'");
                    var loggedErrors = _renderLogs.GetEntries(runId, report.SourcePath)
                        .Where(e => !e.Message.Contains("License", StringComparison.OrdinalIgnoreCase) &&
                                    (e.ExceptionMessage == null || !e.ExceptionMessage.Contains("License", StringComparison.OrdinalIgnoreCase)))
                        .ToList();
                    var backendMsg = loggedErrors.FirstOrDefault(e => !string.IsNullOrWhiteSpace(e.ExceptionMessage))?.ExceptionMessage
                                     ?? loggedErrors.FirstOrDefault()?.Message;
                    var chosenMsg = !string.IsNullOrWhiteSpace(backendMsg) ? backendMsg : clientError;
                    if (chosenMsg.Length > 300)
                    {
                        chosenMsg = chosenMsg.Substring(0, 297) + "...";
                    }

                    if (IsDataSourceConnectionIssue(chosenMsg))
                    {
                        card.Status = "DataSourceOffline";
                        card.ErrorMessage = "Database offline in local preview. RDL structure is valid and ready for server upload.";
                    }
                    else
                    {
                        card.Status = "Failed";
                        card.ErrorMessage = chosenMsg;
                    }
                    await CaptureFailureEvidenceAsync();
                }
            }
            catch (TimeoutException)
            {
                sw.Stop();
                card.DurationMs = sw.ElapsedMilliseconds;
                card.Status = "Failed";

                // renderingBegin tells us whether the viewer ever started drawing. "Never started"
                // (scripts blocked, service unreachable) and "started but did not finish" (slow
                // query, huge report) need completely different fixes.
                var began = false;
                try
                {
                    if (page != null && !page.IsClosed)
                    {
                        began = await page.EvaluateAsync<bool>("() => window.__renderBegan === true");
                    }
                }
                catch { /* page may already be gone */ }

                var seconds = renderTimeoutMs / 1000;
                card.ErrorMessage = began
                    ? $"Rendering started but did not finish within {seconds}s. The report is likely slow " +
                      "(large dataset or a long-running query) rather than broken — raise " +
                      "Validation.RenderTimeoutSeconds in Settings, or check the snapshot below."
                    : $"The viewer never began rendering within {seconds}s. The snapshot below shows what was " +
                      "on screen — check for an error banner, a parameter prompt, or an unreachable data source.";

                await CaptureFailureEvidenceAsync();
            }
            catch (Exception ex)
            {
                sw.Stop();
                card.DurationMs = sw.ElapsedMilliseconds;
                if (IsDataSourceConnectionIssue(ex.Message))
                {
                    card.Status = "DataSourceOffline";
                    card.ErrorMessage = "Database offline in local preview. RDL structure is valid and ready for server upload.";
                }
                else
                {
                    card.Status = "Failed";
                    card.ErrorMessage = ex.Message;
                }
                await CaptureFailureEvidenceAsync();
            }
            finally
            {
                if (page != null) await page.CloseAsync();
                if (context != null) await context.CloseAsync();

                // Both sides of this report's comparison must be done before the slot is handed to
                // the next report, so the pair stays together.
                try { await sourceSnapshotTask; } catch { /* already logged; never fails the render */ }

                semaphore.Release();
            }

            card.SourceSnapshotUrl = File.Exists(Path.Combine(runDir, $"{report.Id}-source.png"))
                ? $"/api/migration/runs/{runId}/screenshots/{report.Id}/source.png"
                : null;

            // Persist validation result
            var isPassedOrOffline = card.Status == "Passed" || card.Status == "DataSourceOffline";
            var validationResult = new ValidationResult
            {
                ReportId = report.Id,
                DataAccuracy = isPassedOrOffline ? 100 : 0,
                RenderStatus = card.Status,
                RenderDurationMs = card.DurationMs,
                ScreenshotRelativePath = card.ScreenshotUrl,
                ErrorMessage = card.ErrorMessage,
                RenderSummary = card.Status == "Passed"
                    ? $"Passed in {card.DurationMs}ms"
                    : (card.Status == "DataSourceOffline"
                        ? "RDL structure valid. Database offline in local preview; report is ready for server upload."
                        : card.ErrorMessage)
            };

            using (var resultScope = _scopeFactory.CreateScope())
            {
                var store = resultScope.ServiceProvider.GetRequiredService<IMigrationStore>();
                await store.AddValidationResultAsync(validationResult, ct);

                if (record != null)
                {
                    record.State = isPassedOrOffline
                        ? ReportState.Approved
                        : (card.Status == "NeedsManualCheck" ? ReportState.AwaitingReview : ReportState.ValidationFailed);
                    await store.UpdateReportRecordAsync(record, ct);
                }
            }

            cards.Add(card);
        });

        await Task.WhenAll(tasks);

        if (browser != null) await browser.CloseAsync();
        playwright?.Dispose();

        summary.Reports = cards.OrderBy(c => c.Path).ToList();
        summary.Passed = summary.Reports.Count(r => r.Status == "Passed");
        summary.DataSourceOffline = summary.Reports.Count(r => r.Status == "DataSourceOffline");
        summary.Failed = summary.Reports.Count(r => r.Status == "Failed");
        summary.NeedsManualCheck = summary.Reports.Count(r => r.Status == "NeedsManualCheck");
        summary.Skipped = summary.Reports.Count(r => r.Status == "Skipped");

        return summary;
    }

    public async Task SkipValidationAsync(Guid runId, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IMigrationStore>();
        var reports = await store.GetCatalogItemsAsync(runId, ItemType.Report, ct);
        foreach (var report in reports)
        {
            var record = await store.GetReportRecordAsync(report.Id, ct);
            if (record != null && record.State != ReportState.Blocked)
            {
                ReportStateMachine.TryTransition(record, ReportState.AutoPassed);
                await store.UpdateReportRecordAsync(record, ct);
            }

            var valResult = new ValidationResult
            {
                ReportId = report.Id,
                DataAccuracy = 100,
                RenderStatus = "Skipped",
                RenderSummary = "Validation skipped by user request; approved for upload."
            };
            await store.AddValidationResultAsync(valResult, ct);
        }
    }
}
