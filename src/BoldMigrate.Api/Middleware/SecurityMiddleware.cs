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

    private static void AddSecurityHeaders(HttpResponse response)
    {
        response.Headers.TryAdd("X-Content-Type-Options", "nosniff");
        response.Headers.TryAdd("X-Frame-Options", "SAMEORIGIN");
        response.Headers.TryAdd("Referrer-Policy", "no-referrer");
        response.Headers.TryAdd("Permissions-Policy", "camera=(), microphone=(), geolocation=()");
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
