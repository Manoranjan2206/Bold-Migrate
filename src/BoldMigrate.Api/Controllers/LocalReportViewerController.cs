using System.Net;
using BoldMigrate.Api.Preview;
using BoldReports.Web;
using BoldReports.Web.ReportViewer;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;

namespace BoldMigrate.Api.Controllers;

/// <summary>
/// Report Viewer service for Step 4. Renders a migration run's staged reports straight from
/// local staging (<c>/api/preview/runs/{runId}/...</c>), so they can be previewed before upload. Data source
/// credentials come from Step 1; when none were given, the viewer prompts at run time.
/// </summary>
[Route("api/preview/runs/{runId:guid}/viewer/[action]")]
public class LocalReportViewerController : ControllerBase, IReportController, IReportHelperSettings
{
    private readonly IMemoryCache _cache;
    private readonly StagedReportSession _session;
    private StagedReportServer? _server;

    public LocalReportViewerController(IMemoryCache cache, StagedReportSession session)
    {
        _cache = cache;
        _session = session;
    }

    [NonAction]
    public void InitializeSettings(ReportHelperSettings helperSettings)
    {
        helperSettings.ReportingServer = _server;
    }

    [HttpPost]
    public async Task<object> PostReportAction([FromRoute] Guid runId, [FromBody] Dictionary<string, object> jsonResult)
    {
        if (!await BindRunAsync(runId)) return RunNotFound();
        return ReportHelper.ProcessReport(jsonResult, this, _cache);
    }

    [HttpPost]
    public async Task<object> PostFormReportAction([FromRoute] Guid runId)
    {
        if (!await BindRunAsync(runId)) return RunNotFound();
        return ReportHelper.ProcessReport(null, this, _cache);
    }

    [HttpGet]
    public async Task<object> GetResource([FromRoute] Guid runId, ReportResource resource)
    {
        if (!await BindRunAsync(runId)) return RunNotFound();
        return ReportHelper.GetResource(resource, this, _cache);
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
        // Content comes from the staged server. Bold Reports switches to server mode when a
        // server URL is set, so a placeholder is given (as in the official external-server sample).
        reportOption.ReportModel.ReportingServer = _server;
        reportOption.ReportModel.ReportServerUrl = StagedReportServer.PlaceholderServerUrl;
        reportOption.ReportModel.ReportServerCredential = new NetworkCredential("staged", "staged");
    }

    [NonAction]
    public void OnReportLoaded(ReportViewerOptions reportOption)
    {
    }

    private async Task<bool> BindRunAsync(Guid runId)
    {
        _server = runId == Guid.Empty ? null : await _session.CreateServerAsync(runId, HttpContext.RequestAborted);
        return _server != null;
    }

    private NotFoundObjectResult RunNotFound() =>
        NotFound(new { message = "Unknown or missing runId. Open the viewer from Step 4 of an active migration run." });
}
