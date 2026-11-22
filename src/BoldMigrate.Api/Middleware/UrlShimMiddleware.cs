using BoldMigrate.Core.Configuration;
using BoldMigrate.Core.Interfaces;
using BoldMigrate.Engine.Logging;

namespace BoldMigrate.Api.Middleware;

public class UrlShimMiddleware
{
    private readonly RequestDelegate _next;
    private readonly MigrationSettings _settings;

    public UrlShimMiddleware(RequestDelegate next, MigrationSettings settings)
    {
        _next = next;
        _settings = settings;
    }

    public async Task InvokeAsync(HttpContext context, IMigrationStore store, MigrationLogger logger)
    {
        var path = context.Request.Path.Value ?? "";

        // Check if shim is enabled and path matches
        var listenPath = _settings.UrlShimListenPath.TrimEnd('/');
        var isReportServer = path.StartsWith(listenPath, StringComparison.OrdinalIgnoreCase);
        var isReportManager = path.StartsWith("/Reports/Pages/Report.aspx", StringComparison.OrdinalIgnoreCase);

        if (!_settings.UrlShimEnabled && !isReportServer && !isReportManager)
        {
            await _next(context);
            return;
        }

        if (!isReportServer && !isReportManager)
        {
            await _next(context);
            return;
        }

        // Extract requested report path
        string requestedReportPath = "";

        if (isReportManager)
        {
            requestedReportPath = context.Request.Query["ItemPath"].ToString();
        }
        else
        {
            // /ReportServer?/Finance/Monthly/Invoice Summary
            var query = context.Request.QueryString.Value ?? "";
            if (query.StartsWith("?/") || query.StartsWith("?%2F"))
            {
                var endIdx = query.IndexOf('&');
                var pathToken = endIdx > 0 ? query.Substring(1, endIdx - 1) : query.Substring(1);
                requestedReportPath = Uri.UnescapeDataString(pathToken);
            }
            else if (path.Length > listenPath.Length)
            {
                requestedReportPath = path.Substring(listenPath.Length);
            }
        }

        requestedReportPath = requestedReportPath.Trim();

        // Check against active runs' URL mappings
        var activeRun = await store.GetActiveRunAsync(context.RequestAborted);
        var mappings = activeRun != null
            ? await store.GetUrlMappingsAsync(activeRun.Id, context.RequestAborted)
            : Array.Empty<BoldMigrate.Core.Models.UrlMapping>();

        var mapping = mappings.FirstOrDefault(m => string.Equals(m.SourcePath, requestedReportPath, StringComparison.OrdinalIgnoreCase) ||
                                                   string.Equals(m.SourcePath.TrimStart('/'), requestedReportPath.TrimStart('/'), StringComparison.OrdinalIgnoreCase));

        if (mapping != null)
        {
            // Build destination redirect URL
            var destUrl = mapping.DestinationUrl;

            // Check format
            var format = context.Request.Query["rs:Format"].ToString();
            if (!string.IsNullOrEmpty(format))
            {
                destUrl += (destUrl.Contains('?') ? "&" : "?") + $"exportFormat={Uri.EscapeDataString(format)}";
            }

            // Forward parameter queries
            foreach (var q in context.Request.Query)
            {
                if (!q.Key.StartsWith("rs:", StringComparison.OrdinalIgnoreCase) &&
                    !q.Key.StartsWith("rc:", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(q.Key, "ItemPath", StringComparison.OrdinalIgnoreCase))
                {
                    destUrl += (destUrl.Contains('?') ? "&" : "?") + $"{Uri.EscapeDataString(q.Key)}={Uri.EscapeDataString(q.Value.ToString())}";
                }
            }

            logger.Log(
                LogLevel.Debug,
                "SHIM-HIT",
                activeRun?.Id ?? Guid.Empty,
                $"URL shim redirected '{requestedReportPath}' to '{destUrl}'.",
                itemPath: requestedReportPath,
                outcome: "Redirect");

            context.Response.Redirect(destUrl, permanent: false);
            return;
        }

        // Not mapped: Return friendly diagnostic page per specification
        logger.Log(
            LogLevel.Warning,
            "SHIM-MISS",
            activeRun?.Id ?? Guid.Empty,
            $"URL shim could not resolve path '{requestedReportPath}'.",
            itemPath: requestedReportPath,
            outcome: "Miss");

        context.Response.StatusCode = 404;
        context.Response.ContentType = "text/html; charset=utf-8";

        var html = $@"<!DOCTYPE html>
<html lang=""en"">
<head>
  <meta charset=""utf-8"">
  <title>Report Not Migrated</title>
  <style>
    body {{ font-family: Inter, Segoe UI, sans-serif; background: #F4F6FA; color: #0B1220; display: flex; align-items: center; justify-content: center; height: 100vh; margin: 0; }}
    .card {{ background: #FFFFFF; border: 1px solid #E3E8EF; border-radius: 14px; padding: 32px; max-width: 560px; box-shadow: 0 4px 12px rgba(0,0,0,0.05); }}
    h1 {{ font-size: 21px; margin-top: 0; color: #B91C1C; }}
    p {{ font-size: 15px; line-height: 1.6; color: #4B5A6E; }}
    code {{ background: #E8EEFD; color: #1D4ED8; padding: 3px 6px; border-radius: 6px; font-size: 13.5px; }}
    a.btn {{ display: inline-block; margin-top: 16px; background: #1D4ED8; color: #FFFFFF; text-decoration: none; padding: 10px 18px; border-radius: 9px; font-weight: 600; font-size: 14px; }}
  </style>
</head>
<body>
  <div class=""card"">
    <h1>Report not migrated</h1>
    <p>The requested report <code>{System.Net.WebUtility.HtmlEncode(requestedReportPath)}</code> was not migrated into Bold Reports or the link is no longer valid.</p>
    <p>Please contact your report administrator or navigate to the Bold Reports home directory to find your reports.</p>
    <a href=""/"" class=""btn"">Go to Bold Reports</a>
  </div>
</body>
</html>";

        await context.Response.WriteAsync(html);
    }
}
