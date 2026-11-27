using System.Security.Cryptography;
using System.Text;
using BoldMigrate.Core.Configuration;

namespace BoldMigrate.Api.Middleware;

/// <summary>
/// Applies security response headers, optional API access token authentication,
/// and double-submit CSRF protection for browser requests.
/// </summary>
public sealed class SecurityMiddleware
{
    public const string CsrfCookieName = "bm_csrf";
    public const string CsrfHeaderName = "X-BoldMigrate-Csrf";

    private readonly RequestDelegate _next;
    private readonly MigrationSettings _settings;

    private static readonly string[] MutatingMethods = ["POST", "PUT", "PATCH", "DELETE"];

    public SecurityMiddleware(RequestDelegate next, MigrationSettings settings)
    {
        _next = next;
        _settings = settings;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        AddSecurityHeaders(context.Response);
        var csrfToken = EnsureCsrfCookie(context);

        var path = context.Request.Path.Value ?? string.Empty;
        var isApi = path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase);

        if (isApi && !string.IsNullOrWhiteSpace(_settings.ApiAccessToken))
        {
            if (!IsAuthorizedByApiToken(context.Request, _settings.ApiAccessToken))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsJsonAsync(new { message = "Unauthorized: valid API access token required." });
                return;
            }
        }

        if (isApi && IsMutatingMethod(context.Request.Method) && !IsCsrfExempt(path))
        {
            if (!ValidateCsrf(context.Request, csrfToken))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new { message = "Forbidden: CSRF token validation failed." });
                return;
            }
        }

        await _next(context);
    }

    /// <summary>
    /// Content Security Policy for the console.
    ///
    /// 'unsafe-inline' is still required for script-src because index.html and the JS renderers
    /// use inline event handlers; removing those is tracked separately. The policy is still
    /// worth setting: connect-src 'self' blocks exfiltration of SSRS credentials, Bold Reports
    /// tokens and SQL passwords to an attacker-controlled host, object-src 'none' and
    /// base-uri 'self' close two common injection vectors, and script-src pins executable code
    /// to this origin plus the two Bold Reports / cdnjs origins the viewer and designer need.
    /// </summary>
    private static string BuildContentSecurityPolicy(string? destinationOrigin)
    {
        // The embedded Bold Reports Viewer/Designer call the DESTINATION server directly from the
        // browser, so a flat `connect-src 'self'` would block exactly the "open on server" feature.
        // The operator's configured destination origin is added to the directives the widgets use.
        var dest = string.IsNullOrWhiteSpace(destinationOrigin) ? string.Empty : " " + destinationOrigin;

        return
            "default-src 'self'; " +
            $"script-src 'self' 'unsafe-inline' 'unsafe-eval' https://cdnjs.cloudflare.com https://cdn.boldreports.com{dest}; " +
            $"style-src 'self' 'unsafe-inline' https://fonts.googleapis.com https://cdnjs.cloudflare.com https://cdn.boldreports.com{dest}; " +
            $"font-src 'self' data: https://fonts.gstatic.com https://cdn.boldreports.com{dest}; " +
            $"img-src 'self' data: blob: https://cdn.boldreports.com{dest}; " +
            $"connect-src 'self'{dest}; " +
            $"frame-src 'self'{dest}; " +
            "worker-src 'self' blob:; " +
            "object-src 'none'; " +
            "base-uri 'self'; " +
            "form-action 'self'; " +
            "frame-ancestors 'self'";
    }

    private void AddSecurityHeaders(HttpResponse response)
    {
        response.Headers.TryAdd("X-Content-Type-Options", "nosniff");
        response.Headers.TryAdd("X-Frame-Options", "SAMEORIGIN");
        response.Headers.TryAdd("Referrer-Policy", "no-referrer");
        response.Headers.TryAdd("Permissions-Policy", "camera=(), microphone=(), geolocation=()");
        response.Headers.TryAdd("Content-Security-Policy", BuildContentSecurityPolicy(ResolveDestinationOrigin()));
    }

    /// <summary>
    /// Origin of the configured Bold Reports destination server, or null when none is set.
    /// </summary>
    private string? ResolveDestinationOrigin()
    {
        var configured = _settings.DestinationServerUrl;
        if (string.IsNullOrWhiteSpace(configured)) return null;

        return Uri.TryCreate(configured, UriKind.Absolute, out var uri)
            ? $"{uri.Scheme}://{uri.Authority}"
            : null;
    }

    private static string EnsureCsrfCookie(HttpContext context)
    {
        if (context.Request.Cookies.TryGetValue(CsrfCookieName, out var existing) &&
            !string.IsNullOrWhiteSpace(existing))
        {
            return existing;
        }

        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        context.Response.Cookies.Append(CsrfCookieName, token, new CookieOptions
        {
            HttpOnly = false, // Must be readable by same-origin UI scripts to echo in X-BoldMigrate-Csrf
            SameSite = SameSiteMode.Strict,
            Secure = context.Request.IsHttps,
            Path = "/"
        });
        return token;
    }

    private static bool IsAuthorizedByApiToken(HttpRequest request, string expectedToken)
    {
        var provided = string.Empty;
        if (request.Headers.TryGetValue("Authorization", out var authHeader))
        {
            var raw = authHeader.ToString();
            if (raw.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                provided = raw["Bearer ".Length..].Trim();
            }
        }
        else if (request.Headers.TryGetValue("X-Api-Key", out var apiKey))
        {
            provided = apiKey.ToString().Trim();
        }
        else if (request.Query.TryGetValue("access_token", out var qToken))
        {
            provided = qToken.ToString().Trim();
        }

        if (string.IsNullOrEmpty(provided))
        {
            return false;
        }

        var expectedBytes = Encoding.UTF8.GetBytes(expectedToken);
        var providedBytes = Encoding.UTF8.GetBytes(provided);
        return CryptographicOperations.FixedTimeEquals(expectedBytes, providedBytes);
    }

    private static bool IsMutatingMethod(string method) =>
        MutatingMethods.Any(m => string.Equals(m, method, StringComparison.OrdinalIgnoreCase));

    private static bool IsCsrfExempt(string path)
    {
        // Bold Reports viewer/designer widgets make internal POST calls without custom app headers;
        // same-origin checks still protect them below when Origin is present.
        return path.StartsWith("/api/preview/", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("/api/localreportviewer/", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("/api/localreportdesigner/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ValidateCsrf(HttpRequest request, string expectedCookieToken)
    {
        if (request.Headers.TryGetValue(CsrfHeaderName, out var headerVal))
        {
            var headerToken = headerVal.ToString().Trim();
            if (!string.IsNullOrEmpty(headerToken) &&
                request.Cookies.ContainsKey(CsrfCookieName))
            {
                var a = Encoding.UTF8.GetBytes(expectedCookieToken);
                var b = Encoding.UTF8.GetBytes(headerToken);
                return CryptographicOperations.FixedTimeEquals(a, b);
            }
            return false;
        }

        // Allow non-browser / integration API clients (no Origin and no browser Sec-Fetch-Site)
        var hasOrigin = request.Headers.TryGetValue("Origin", out var originHeader) && !string.IsNullOrWhiteSpace(originHeader);
        var hasSecFetch = request.Headers.TryGetValue("Sec-Fetch-Site", out var secFetch) && !string.IsNullOrWhiteSpace(secFetch);

        if (!hasOrigin && !hasSecFetch)
        {
            return true;
        }

        // Reject cross-site browser requests missing X-BoldMigrate-Csrf
        if (hasSecFetch && string.Equals(secFetch.ToString(), "cross-site", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (hasOrigin && Uri.TryCreate(originHeader.ToString(), UriKind.Absolute, out var originUri))
        {
            var hostMatch = string.Equals(originUri.Host, request.Host.Host, StringComparison.OrdinalIgnoreCase);
            var portMatch = (originUri.Port == (request.Host.Port ?? (request.IsHttps ? 443 : 80)));
            return hostMatch && portMatch;
        }

        return false;
    }
}
