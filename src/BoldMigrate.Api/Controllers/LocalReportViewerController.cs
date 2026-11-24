using System.Net;
using System.Reflection;
using BoldMigrate.Api.Preview;
using BoldMigrate.Core.Models;
using BoldMigrate.Engine.Preview;
using BoldReports.Web;
using BoldReports.Web.ReportViewer;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;

namespace BoldMigrate.Api.Controllers;

/// <summary>
/// Report Viewer service for Step 4. Renders a migration run's staged reports straight from
/// local staging (<c>/api/preview/runs/{runId}/...</c>), so they can be previewed before upload. Data source
/// credentials come from Step 1; when none were given, the viewer prompts at run time.
/// Implements IReportLogger to capture runtime rendering errors into the RenderLogStore.
/// </summary>
[Route("api/preview/runs/{runId:guid}/viewer/[action]")]
public class LocalReportViewerController : ControllerBase, IReportController, IReportHelperSettings, IReportLogger
{
    private readonly IMemoryCache _cache;
    private readonly StagedReportSession _session;
    private readonly IRenderLogStore _logStore;
    private readonly ILogger<LocalReportViewerController> _logger;
    private StagedReportServer? _server;

    private Guid _currentRunId;
    private string? _currentReportPath;

    public LocalReportViewerController(
        IMemoryCache cache,
        StagedReportSession session,
        IRenderLogStore logStore,
        ILogger<LocalReportViewerController> logger)
    {
        _cache = cache;
        _session = session;
        _logStore = logStore;
        _logger = logger;
    }

    [NonAction]
    public void InitializeSettings(ReportHelperSettings helperSettings)
    {
        helperSettings.ReportingServer = _server;
    }

    [HttpPost]
    public async Task<object> PostReportAction([FromRoute] Guid runId, [FromBody] Dictionary<string, object> jsonResult)
    {
        _currentRunId = runId;
        if (jsonResult != null && jsonResult.TryGetValue("reportPath", out var rp))
        {
            _currentReportPath = rp?.ToString();
        }

        if (!await BindRunAsync(runId)) return RunNotFound();
        return ReportHelper.ProcessReport(jsonResult, this, _cache);
    }

    [HttpPost]
    public async Task<object> PostFormReportAction([FromRoute] Guid runId)
    {
        _currentRunId = runId;
        if (!await BindRunAsync(runId)) return RunNotFound();
        return ReportHelper.ProcessReport(null, this, _cache);
    }

    [HttpGet]
    public async Task<object> GetResource([FromRoute] Guid runId, ReportResource resource)
    {
        _currentRunId = runId;
        if (!await BindRunAsync(runId)) return RunNotFound();
        return ReportHelper.GetResource(resource, this, _cache);
    }

    [HttpGet]
    [ActionName("render-logs")]
    public IActionResult GetRenderLogs([FromRoute] Guid runId, [FromQuery] string? reportPath)
    {
        if (string.IsNullOrWhiteSpace(reportPath))
        {
            var all = _logStore.GetAllEntriesForRun(runId);
            return Ok(all);
        }

        var entries = _logStore.GetEntries(runId, reportPath);
        return Ok(entries);
    }

    // IReportController members; the HTTP endpoints above bind the run first, then do the same.
    [NonAction]
    public object PostReportAction(Dictionary<string, object> jsonResult) => ReportHelper.ProcessReport(jsonResult, this, _cache);

    [NonAction]
    public object PostFormReportAction() => ReportHelper.ProcessReport(null, this, _cache);

    [NonAction]
    public object GetResource(ReportResource resource) => ReportHelper.GetResource(resource, this, _cache);

    [NonAction]
    public void OnInitReportOptions(ReportViewerOptions reportOption)
    {
        _currentReportPath = reportOption.ReportModel.ReportPath;

        // Content comes from the staged server. Bold Reports switches to server mode when a
        // server URL is set, so a placeholder is given (as in the official external-server sample).
        reportOption.ReportModel.ReportingServer = _server;
        reportOption.ReportModel.ReportServerUrl = StagedReportServer.PlaceholderServerUrl;
        reportOption.ReportModel.ReportServerCredential = new NetworkCredential("staged", "staged");
    }

    [NonAction]
    public void OnReportLoaded(ReportViewerOptions reportOption)
    {
        if (!string.IsNullOrWhiteSpace(reportOption.ReportModel.ReportPath))
        {
            _currentReportPath = reportOption.ReportModel.ReportPath;
        }
    }

    [NonAction]
    public void LogError(string message, Exception exception, MethodBase methodType, ErrorType errorType)
    {
        if (message?.Contains("License", StringComparison.OrdinalIgnoreCase) == true ||
            exception?.Message?.Contains("License", StringComparison.OrdinalIgnoreCase) == true)
        {
            return;
        }

        var entry = new RenderLogEntry
        {
            TimestampUtc = DateTime.UtcNow,
            Message = message ?? string.Empty,
            ExceptionMessage = exception?.Message,
            StackTrace = exception?.StackTrace,
            MethodName = methodType?.Name,
            ErrorType = errorType.ToString()
        };

        _logStore.AddEntry(_currentRunId, _currentReportPath ?? "unknown", entry);
        _logger.LogWarning("Local viewer error [{Type}] for run {RunId}, report '{Path}': {Message} in {Method} (Exception: {Ex})",
            errorType, _currentRunId, _currentReportPath, message, methodType?.Name, exception?.Message);
    }

    [NonAction]
    public void LogError(string param1, string param2, Exception exception, string param4, string param5, string param6)
    {
        if (param5?.Contains("License", StringComparison.OrdinalIgnoreCase) == true ||
            param2?.Contains("License", StringComparison.OrdinalIgnoreCase) == true ||
            exception?.Message?.Contains("License", StringComparison.OrdinalIgnoreCase) == true)
        {
            return;
        }

        var entry = new RenderLogEntry
        {
            TimestampUtc = DateTime.UtcNow,
            Message = param5 ?? param2 ?? string.Empty,
            ExceptionMessage = exception?.Message ?? param6,
            StackTrace = exception?.StackTrace,
            MethodName = $"{param1}.{param2}",
            ErrorType = param4 ?? "Error"
        };

        _logStore.AddEntry(_currentRunId, _currentReportPath ?? "unknown", entry);
        _logger.LogWarning("Local viewer error [{Type}] for run {RunId}, report '{Path}': {Message} in {Class}.{Method} (Exception: {Ex})",
            param4, _currentRunId, _currentReportPath, entry.Message, param1, param2, exception?.Message);
    }

    private async Task<bool> BindRunAsync(Guid runId)
    {
        _currentRunId = runId;
        _server = runId == Guid.Empty ? null : await _session.CreateServerAsync(runId, HttpContext.RequestAborted);
        return _server != null;
    }

    private NotFoundObjectResult RunNotFound() =>
        NotFound(new { message = "Unknown or missing runId. Open the viewer from Step 4 of an active migration run." });
}
