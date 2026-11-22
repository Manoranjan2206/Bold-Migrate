namespace BoldMigrate.Core.Common;

/// <summary>
/// Resolves localhost/127.0.0.1 to host.docker.internal when running inside Docker containers
/// so that host-hosted services (SSRS, Bold Reports) are seamlessly reachable.
/// </summary>
public static class HostResolver
{
    public static string Resolve(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return url ?? string.Empty;

        if (IsRunningInContainer())
        {
            return url.Replace("://localhost:", "://host.docker.internal:")
                      .Replace("://127.0.0.1:", "://host.docker.internal:")
                      .Replace("://localhost/", "://host.docker.internal/")
                      .Replace("://127.0.0.1/", "://host.docker.internal/");
        }

        return url;
    }

    public static bool IsRunningInContainer()
    {
        var env = Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER");
        if (string.Equals(env, "true", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        try
        {
            if (File.Exists("/.dockerenv"))
            {
                return true;
            }
        }
        catch
        {
            // Ignore filesystem security exceptions
        }

        return false;
    }
}
