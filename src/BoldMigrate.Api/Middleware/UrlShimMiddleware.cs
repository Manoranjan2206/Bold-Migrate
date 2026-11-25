using BoldMigrate.Core.Configuration;
using BoldMigrate.Core.Interfaces;
using BoldMigrate.Engine.Logging;
using BoldMigrate.Engine.Metrics;

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

        // Check if shim is enabled first; if disabled, pass straight through.
        if (!_settings.UrlShimEnabled)
        {
            await _next(context);
            return;
        }

        var listenPath = (_settings.UrlShimListenPath ?? "/ReportServer").TrimEnd('/');
        var isReportServer = path.StartsWith(listenPath, StringComparison.OrdinalIgnoreCase);
        var isReportManager = path.StartsWith("/Reports/Pages/Report.aspx", StringComparison.OrdinalIgnoreCase);

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

        // Search non-abandoned runs (most recent first, including CutOver runs where the shim operates)
        var candidateRuns = (await store.ListRunsAsync(context.RequestAborted))
            .Where(r => r.Status != BoldMigrate.Core.Enums.RunStatus.Abandoned)
            .ToList();

        BoldMigrate.Core.Models.MigrationRun? matchedRun = null;
        BoldMigrate.Core.Models.UrlMapping? mapping = null;
        foreach (var candidate in candidateRuns)
        {
            var runMappings = await store.GetUrlMappingsAsync(candidate.Id, context.RequestAborted);
            var hit = runMappings.FirstOrDefault(m =>
                string.Equals(m.SourcePath, requestedReportPath, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(m.SourcePath.TrimStart('/'), requestedReportPath.TrimStart('/'), StringComparison.OrdinalIgnoreCase));
            if (hit != null)
            {
                matchedRun = candidate;
                mapping = hit;
                break;
            }
        }

        if (mapping != null)
        {
            if (!IsSafeRedirectDestination(mapping.DestinationUrl, matchedRun?.DestinationServerUrl))
            {
                MigrationMetrics.ShimRequestsTotal.Add(1,
                    new KeyValuePair<string, object?>("outcome", "BlockedUnsafeRedirect"));
                logger.Log(
                    LogLevel.Warning,
                    "SHIM-BLOCKED",
                    matchedRun?.Id ?? Guid.Empty,
                    $"Blocked unsafe URL shim redirect target '{mapping.DestinationUrl}'.",
                    itemPath: requestedReportPath,
                    outcome: "Blocked");
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsync("Unsafe redirect destination rejected.");
                return;
            }

            // Build destination redirect URL
            var destUrl = mapping.DestinationUrl;

            // Check format
            var format = context.Request.Query["rs:Format"].ToString();
            if (!string.IsNullOrEmpty(format))
            {
                destUrl += (destUrl.Contains('?') ? "&" : "?") + $"exportFormat={Uri.EscapeDataString(format)}";
            }

            // Forward parameter queries (excluding SSRS control params and the report path token itself)
            foreach (var q in context.Request.Query)
            {
                if (!q.Key.StartsWith('/') &&
                    !q.Key.StartsWith("rs:", StringComparison.OrdinalIgnoreCase) &&
                    !q.Key.StartsWith("rc:", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(q.Key, "ItemPath", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(q.Key, requestedReportPath, StringComparison.OrdinalIgnoreCase))
                {
                    destUrl += (destUrl.Contains('?') ? "&" : "?") + $"{Uri.EscapeDataString(q.Key)}={Uri.EscapeDataString(q.Value.ToString())}";
                }
            }

            MigrationMetrics.ShimRequestsTotal.Add(1,
                new KeyValuePair<string, object?>("outcome", "Redirect"));
            logger.Log(
                LogLevel.Debug,
                "SHIM-HIT",
                matchedRun?.Id ?? Guid.Empty,
                $"URL shim redirected '{requestedReportPath}' to '{destUrl}'.",
                itemPath: requestedReportPath,
                outcome: "Redirect");

            context.Response.Redirect(destUrl, permanent: false);
            return;
        }

        // Not mapped: Return friendly diagnostic page per specification
        MigrationMetrics.ShimRequestsTotal.Add(1,
            new KeyValuePair<string, object?>("outcome", "Miss"));
        logger.Log(
            LogLevel.Warning,
            "SHIM-MISS",
            candidateRuns.FirstOrDefault()?.Id ?? Guid.Empty,
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

    private static bool IsSafeRedirectDestination(string? destUrl, string? configuredDestinationServerUrl)
    {
        if (string.IsNullOrWhiteSpace(destUrl)) return false;

        // Relative paths within the app are safe, except protocol-relative // or /\
        if (destUrl.StartsWith('/') && !destUrl.StartsWith("//") && !destUrl.StartsWith("/\\"))
        {
            return true;
        }

        if (!Uri.TryCreate(destUrl, UriKind.Absolute, out var destUri)) return false;
        if (destUri.Scheme != Uri.UriSchemeHttp && destUri.Scheme != Uri.UriSchemeHttps) return false;

        if (!string.IsNullOrWhiteSpace(configuredDestinationServerUrl) &&
            Uri.TryCreate(configuredDestinationServerUrl, UriKind.Absolute, out var expectedUri))
        {
            return string.Equals(destUri.Scheme, expectedUri.Scheme, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(destUri.Host, expectedUri.Host, StringComparison.OrdinalIgnoreCase) &&
                   destUri.Port == expectedUri.Port;
        }

        return false;
    }
}
