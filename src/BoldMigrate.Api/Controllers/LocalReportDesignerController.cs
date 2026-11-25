using System.Net;
using System.Text.RegularExpressions;
using BoldMigrate.Api.Preview;
using BoldReports.Web;
using BoldReports.Web.ReportDesigner;
using BoldReports.Web.ReportViewer;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;

namespace BoldMigrate.Api.Controllers;

/// <summary>
/// Report Designer service for Step 4. Opens a migration run's staged reports (<c>/api/preview/runs/{runId}/...</c>)
/// for editing; Save writes the report back into staging and marks it for the next upload.
/// </summary>
[Route("api/preview/runs/{runId:guid}/designer/[action]")]
public class LocalReportDesignerController : ControllerBase, IReportDesignerController, IReportHelperSettings
{
    // Designer upload keys and item ids become file names in the designer cache folder.
    private static readonly Regex SafeCacheName = new("^[A-Za-z0-9._ -]{1,128}$", RegexOptions.Compiled);

    private readonly IMemoryCache _cache;
    private readonly StagedReportSession _session;
    private readonly string _designerCacheRoot;
    private StagedReportServer? _server;

    public LocalReportDesignerController(IMemoryCache cache, StagedReportSession session, BoldMigrate.Core.Interfaces.IStagingStore staging)
    {
        _cache = cache;
        _session = session;
        // GetRunStagingPath(Guid.Empty) is the staging root plus a fixed folder name, never a real run.
        _designerCacheRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(staging.GetRunStagingPath(Guid.Empty))!, "_designer-cache"));
    }

    [NonAction]
    public void InitializeSettings(ReportHelperSettings helperSettings)
    {
        helperSettings.ReportingServer = _server;
    }

    [HttpPost]
    public async Task<object> PostDesignerAction([FromRoute] Guid runId, [FromBody] Dictionary<string, object> jsonResult)
    {
        if (!await BindRunAsync(runId)) return RunNotFound();
        return ReportDesignerHelper.ProcessDesigner(jsonResult, this, null, _cache);
    }

    [HttpPost]
    public async Task<object> PostFormDesignerAction([FromRoute] Guid runId)
    {
        if (!await BindRunAsync(runId)) return RunNotFound();
        return ReportDesignerHelper.ProcessDesigner(null, this, null, _cache);
    }

    [HttpPost]
    public async Task<object> UploadReportAction([FromRoute] Guid runId)
    {
        if (!await BindRunAsync(runId)) return RunNotFound();
        var file = Request.HasFormContentType ? Request.Form.Files.FirstOrDefault() : null;
        return ReportDesignerHelper.ProcessDesigner(null, this, file, _cache);
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

    // Designer cache images are keyed per designer session; the run id scopes and authorizes the route.
    [HttpGet]
    public async Task<object> GetImage([FromRoute] Guid runId, string key, string image)
    {
        if (!await BindRunAsync(runId)) return RunNotFound();
        return ReportDesignerHelper.GetImage(key, image, this);
    }

    [NonAction]
    public object GetImage(string key, string image) => ReportDesignerHelper.GetImage(key, image, this);

    // IReportDesignerController / IReportController members; the HTTP endpoints above bind the
    // run first, then do the same.
    [NonAction]
    public object PostDesignerAction(Dictionary<string, object> jsonResult) => ReportDesignerHelper.ProcessDesigner(jsonResult, this, null, _cache);

    [NonAction]
    public object PostFormDesignerAction() => ReportDesignerHelper.ProcessDesigner(null, this, null, _cache);

    [NonAction]
    public void UploadReportAction() => ReportDesignerHelper.ProcessDesigner(null, this, Request.Form.Files[0], _cache);

    [NonAction]
    public object PostReportAction(Dictionary<string, object> jsonResult) => ReportHelper.ProcessReport(jsonResult, this, _cache);

    [NonAction]
    public object PostFormReportAction() => ReportHelper.ProcessReport(null, this, _cache);

    [NonAction]
    public object GetResource(ReportResource resource) => ReportHelper.GetResource(resource, this, _cache);

    [NonAction]
    public void OnInitReportOptions(ReportViewerOptions reportOption)
    {
        reportOption.ReportModel.ReportingServer = _server;
        reportOption.ReportModel.ReportServerUrl = StagedReportServer.PlaceholderServerUrl;
        reportOption.ReportModel.ReportServerCredential = new NetworkCredential("staged", "staged");
    }

    [NonAction]
    public void OnReportLoaded(ReportViewerOptions reportOption)
    {
    }

    /// <summary>
    /// Designer working files (images, uploaded reports, preview copies). Kept under the
    /// staging root, never in wwwroot, so they are not served as static files.
    /// </summary>
    [NonAction]
    public bool SetData(string key, string itemId, ItemInfo itemData, out string errorMessage)
    {
        errorMessage = string.Empty;
        var name = string.IsNullOrEmpty(itemId) && itemData.PostedFile != null
            ? Path.GetFileName(itemData.PostedFile.FileName)
            : itemId;

        var path = CacheFilePath(key, name);
        if (path == null)
        {
            errorMessage = "Invalid designer cache key or item name.";
            return false;
        }

        if (itemData.Data != null)
        {
            System.IO.File.WriteAllBytes(path, itemData.Data);
        }
        else if (itemData.PostedFile != null)
        {
            using var target = System.IO.File.Create(path);
            itemData.PostedFile.CopyTo(target);
        }

        return true;
    }

    [NonAction]
    public ResourceInfo GetData(string key, string itemId)
    {
        var path = CacheFilePath(key, itemId);
        if (path == null || !System.IO.File.Exists(path))
        {
            return new ResourceInfo { ErrorMessage = "The requested designer resource was not found." };
        }
        return new ResourceInfo { Data = System.IO.File.ReadAllBytes(path) };
    }

    private string? CacheFilePath(string key, string itemName)
    {
        var trimmedKey = key?.Trim() ?? string.Empty;
        var trimmedItem = itemName?.Trim() ?? string.Empty;
        if (!SafeCacheName.IsMatch(trimmedKey) ||
            !SafeCacheName.IsMatch(trimmedItem) ||
            trimmedKey is "." or ".." ||
            trimmedItem is "." or ".." ||
            trimmedKey.Contains("..", StringComparison.Ordinal) ||
            trimmedItem.Contains("..", StringComparison.Ordinal))
        {
            return null;
        }

        var folder = Path.GetFullPath(Path.Combine(_designerCacheRoot, trimmedKey));
        var rootWithSep = _designerCacheRoot.EndsWith(Path.DirectorySeparatorChar)
            ? _designerCacheRoot
            : _designerCacheRoot + Path.DirectorySeparatorChar;
        if (!folder.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var fullFile = Path.GetFullPath(Path.Combine(folder, trimmedItem));
        var folderWithSep = folder.EndsWith(Path.DirectorySeparatorChar)
            ? folder
            : folder + Path.DirectorySeparatorChar;
        if (!fullFile.StartsWith(folderWithSep, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        Directory.CreateDirectory(folder);
        return fullFile;
    }

    private async Task<bool> BindRunAsync(Guid runId)
    {
        _server = runId == Guid.Empty ? null : await _session.CreateServerAsync(runId, HttpContext.RequestAborted);
        return _server != null;
    }

    private NotFoundObjectResult RunNotFound() =>
        NotFound(new { message = "Unknown or missing runId. Open the designer from Step 4 of an active migration run." });
}
