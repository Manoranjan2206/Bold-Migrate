using System.Net;

namespace BoldMigrate.Core.Common;

/// <summary>
/// Validates operator-supplied server URLs before Bold Migrate makes a server-side request to them.
///
/// Bold Migrate exists to talk to internal SSRS/PBIRS and Bold Reports hosts, so a blanket SSRF
/// block is not possible — loopback and private addresses are legitimate targets. What this guard
/// does stop is the class of destination that is never a report server and is the classic SSRF
/// payoff: cloud instance-metadata endpoints and other link-local addresses. On Windows the
/// adapters fall back to <c>CredentialCache.DefaultCredentials</c>, so an unguarded URL would also
/// send the service account's NTLM/Kerberos identity to whatever host was named.
///
/// Operators who want a strict perimeter can additionally set BoldMigrate:AllowedSourceHosts.
/// </summary>
public static class OutboundUrlGuard
{
    /// <summary>Host names that resolve to cloud instance metadata services.</summary>
    private static readonly string[] BlockedHostNames =
    [
        "metadata.google.internal",
        "metadata.goog",
        "instance-data"
    ];

    public sealed record Result(bool Allowed, string? Reason)
    {
        public static readonly Result Ok = new(true, null);
        public static Result Deny(string reason) => new(false, reason);
    }

    /// <summary>
    /// Checks whether Bold Migrate may issue a server-side request to <paramref name="url"/>.
    /// </summary>
    /// <param name="url">The operator-supplied absolute URL.</param>
    /// <param name="allowedHosts">
    /// Optional operator allowlist. When non-empty, the target host must match one of its entries
    /// (case-insensitive, exact host match).
    /// </param>
    public static Result Check(string? url, IReadOnlyCollection<string>? allowedHosts = null)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return Result.Deny("Server URL is required.");
        }

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        {
            return Result.Deny("Server URL is not a valid absolute URL.");
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            return Result.Deny($"Unsupported URL scheme '{uri.Scheme}'. Only http and https are allowed.");
        }

        var host = uri.Host;

        if (BlockedHostNames.Any(h => string.Equals(h, host, StringComparison.OrdinalIgnoreCase)))
        {
            return Result.Deny("Cloud instance metadata endpoints are not valid report servers and are blocked.");
        }

        if (IPAddress.TryParse(host, out var ip) && IsLinkLocal(ip))
        {
            return Result.Deny(
                "Link-local addresses (including cloud instance metadata at 169.254.169.254) are blocked.");
        }

        if (allowedHosts is { Count: > 0 })
        {
            var permitted = allowedHosts
                .Where(h => !string.IsNullOrWhiteSpace(h))
                .Any(h => string.Equals(h.Trim(), host, StringComparison.OrdinalIgnoreCase));

            if (!permitted)
            {
                return Result.Deny($"Host '{host}' is not listed in BoldMigrate:AllowedSourceHosts.");
            }
        }

        return Result.Ok;
    }

    private static bool IsLinkLocal(IPAddress ip)
    {
        if (ip.IsIPv6LinkLocal) return true;

        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var bytes = ip.GetAddressBytes();
            // 169.254.0.0/16 — RFC 3927, and where 169.254.169.254 metadata lives.
            return bytes[0] == 169 && bytes[1] == 254;
        }

        // IPv4-mapped IPv6 (::ffff:169.254.169.254) must not slip past the check above.
        if (ip.IsIPv4MappedToIPv6)
        {
            return IsLinkLocal(ip.MapToIPv4());
        }

        return false;
    }
}
