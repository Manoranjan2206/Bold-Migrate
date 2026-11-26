using System.Text.RegularExpressions;

namespace BoldMigrate.Core.Common;

/// <summary>
/// Shared SQL Server connection string helpers for local preview and Step 1 validation.
/// </summary>
public static partial class SqlConnectionHelper
{
    private static readonly Regex DataSourceKeyRegex = MySqlOrSqlServerHostRegex();

    [GeneratedRegex(@"(?:^|;)\s*(?:Server|Data Source|Addr|Address|Network Address)\s*=\s*([^;]+)", RegexOptions.IgnoreCase)]
    private static partial Regex MySqlOrSqlServerHostRegex();

    public static bool IsSqlServerConnection(string? connectString, string? extension = null)
    {
        if (string.IsNullOrWhiteSpace(connectString)) return false;
        return string.Equals(extension, "SQL", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(extension, "MSSQL", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(extension, "SQLAZURE", StringComparison.OrdinalIgnoreCase) ||
               (!string.IsNullOrWhiteSpace(extension) && extension.Contains("SqlClient", StringComparison.OrdinalIgnoreCase)) ||
               connectString.Contains("Server=", StringComparison.OrdinalIgnoreCase) ||
               connectString.Contains("Data Source=", StringComparison.OrdinalIgnoreCase);
    }

    public static bool HasTrustServerCertificateSetting(string? connectString) =>
        !string.IsNullOrWhiteSpace(connectString) &&
        connectString.Contains("TrustServerCertificate=", StringComparison.OrdinalIgnoreCase);

    public static bool IsLocalOrInternalSqlHost(string? connectString)
    {
        if (string.IsNullOrWhiteSpace(connectString)) return false;
        var match = DataSourceKeyRegex.Match(connectString);
        if (!match.Success) return false;

        var rawServer = match.Groups[1].Value.Trim().Trim('"', '\'');
        if (rawServer.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase))
            rawServer = rawServer[4..];
        if (rawServer.StartsWith("np:", StringComparison.OrdinalIgnoreCase))
            return true;

        var hostOnly = rawServer.Split(',')[0].Split('\\')[0].Trim();
        return hostOnly.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
               hostOnly.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
               hostOnly.Equals("::1", StringComparison.OrdinalIgnoreCase) ||
               hostOnly.Equals("(local)", StringComparison.OrdinalIgnoreCase) ||
               hostOnly.Equals("(localdb)", StringComparison.OrdinalIgnoreCase) ||
               hostOnly.Equals(".", StringComparison.OrdinalIgnoreCase) ||
               hostOnly.Equals("host.docker.internal", StringComparison.OrdinalIgnoreCase) ||
               hostOnly.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase);
    }

    public static bool ShouldTrustSqlCertificate(string? connectString, string? extension, bool allowUntrusted)
    {
        if (!IsSqlServerConnection(connectString, extension)) return false;
        if (HasTrustServerCertificateSetting(connectString)) return false;
        return allowUntrusted || IsLocalOrInternalSqlHost(connectString);
    }

    public static bool IsUntrustedCertificateSqlError(int errorNumber, string? message)
    {
        if (errorNumber == -2146893019) return true;
        return !string.IsNullOrWhiteSpace(message) &&
               (message.Contains("certificate chain was issued by an authority that is not trusted", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("target principal name is incorrect", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Remaps server/host, database/initial catalog, or raw substrings in a connection string,
    /// using DbConnectionStringBuilder when key-value syntax is valid.
    /// </summary>
    public static string RemapConnectionString(
        string? connectString,
        string? findServer,
        string? replaceServer,
        string? findDatabase,
        string? replaceDatabase,
        string? rawFind = null,
        string? rawReplace = null,
        bool? trustServerCertificate = null)
    {
        var current = connectString?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(current)) return current;

        if (!string.IsNullOrEmpty(rawFind))
        {
            current = current.Replace(rawFind, rawReplace ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        var hasServerChange = !string.IsNullOrWhiteSpace(replaceServer) || !string.IsNullOrEmpty(findServer);
        var hasDbChange = !string.IsNullOrWhiteSpace(replaceDatabase) || !string.IsNullOrEmpty(findDatabase);

        if (hasServerChange || hasDbChange || trustServerCertificate.HasValue)
        {
            try
            {
                var builder = new System.Data.Common.DbConnectionStringBuilder { ConnectionString = current };

                if (hasServerChange && !string.IsNullOrEmpty(replaceServer?.Trim()))
                {
                    var serverKeys = new[] { "Data Source", "Server", "Addr", "Address", "Network Address" };
                    var matchedKey = serverKeys.FirstOrDefault(k => builder.ContainsKey(k));
                    if (matchedKey != null)
                    {
                        var val = builder[matchedKey]?.ToString() ?? string.Empty;
                        if (string.IsNullOrWhiteSpace(findServer) ||
                            val.Contains(findServer.Trim(), StringComparison.OrdinalIgnoreCase))
                        {
                            builder[matchedKey] = string.IsNullOrWhiteSpace(findServer)
                                ? replaceServer.Trim()
                                : val.Replace(findServer.Trim(), replaceServer.Trim(), StringComparison.OrdinalIgnoreCase);
                        }
                    }
                }

                if (hasDbChange && !string.IsNullOrEmpty(replaceDatabase?.Trim()))
                {
                    var dbKeys = new[] { "Initial Catalog", "Database" };
                    var matchedKey = dbKeys.FirstOrDefault(k => builder.ContainsKey(k));
                    if (matchedKey != null)
                    {
                        var val = builder[matchedKey]?.ToString() ?? string.Empty;
                        if (string.IsNullOrWhiteSpace(findDatabase) ||
                            val.Contains(findDatabase.Trim(), StringComparison.OrdinalIgnoreCase))
                        {
                            builder[matchedKey] = string.IsNullOrWhiteSpace(findDatabase)
                                ? replaceDatabase.Trim()
                                : val.Replace(findDatabase.Trim(), replaceDatabase.Trim(), StringComparison.OrdinalIgnoreCase);
                        }
                    }
                }

                if (trustServerCertificate == true && IsSqlServerConnection(builder.ConnectionString))
                {
                    builder["TrustServerCertificate"] = true;
                }
                else if (trustServerCertificate == false && builder.ContainsKey("TrustServerCertificate"))
                {
                    builder.Remove("TrustServerCertificate");
                }

                current = builder.ConnectionString;
            }
            catch (ArgumentException)
            {
                // Fallback for non-standard connection strings
                if (!string.IsNullOrWhiteSpace(findServer) && replaceServer != null)
                {
                    current = current.Replace(findServer.Trim(), replaceServer.Trim(), StringComparison.OrdinalIgnoreCase);
                }
                if (!string.IsNullOrWhiteSpace(findDatabase) && replaceDatabase != null)
                {
                    current = current.Replace(findDatabase.Trim(), replaceDatabase.Trim(), StringComparison.OrdinalIgnoreCase);
                }
            }
        }

        return current;
    }
}
