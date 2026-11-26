using System.Text.RegularExpressions;

namespace BoldMigrate.Engine.Logging;

public static class Redactor
{
    // Handles quoted ("a;b" or 'a;b') and unquoted values for sensitive connection string keys.
    private static readonly Regex PasswordKeyRegex = new(
        @"(?<=(?:Password|Pwd|User\s*ID|Uid|AccountKey|SharedAccessKey|ClientSecret|AccessToken|ApiKey)\s*=\s*)(?:""[^""]*""|'[^']*'|[^;\r\n]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex BearerTokenRegex = new(
        @"(Bearer\s+)([A-Za-z0-9\-_]{4})([A-Za-z0-9\-_\.]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex AuthHeaderRegex = new(
        @"(?<=(Authorization\s*:\s*)(Bearer|Basic|Negotiate|NTLM)\s+)([^\r\n]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex JsonSecretRegex = new(
        @"(?<=""(?:password|pwd|secret|token|bearerToken|serviceAuthorizationToken|access_token)""\s*:\s*"")([^""]+)(?="")",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex XmlPasswordRegex = new(
        @"(?<=<PassWord>)([^<]+)(?=</PassWord>)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string RedactConnectionString(string? connectionString)
    {
        if (string.IsNullOrEmpty(connectionString)) return string.Empty;
        return PasswordKeyRegex.Replace(connectionString, "[redacted]");
    }

    public static string RedactSecrets(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        var result = RedactConnectionString(text);
        result = BearerTokenRegex.Replace(result, m => $"{m.Groups[1].Value}{m.Groups[2].Value}[redacted]");
        result = AuthHeaderRegex.Replace(result, "[redacted]");
        result = JsonSecretRegex.Replace(result, "[redacted]");
        result = XmlPasswordRegex.Replace(result, "[redacted]");

        return result;
    }

    public static string RedactBearerToken(string? token)
    {
        if (string.IsNullOrEmpty(token)) return string.Empty;
        if (token.Length <= 4) return "[redacted]";
        return $"{token[..4]}[redacted]";
    }

    public static string RedactAuthHeader(string? authHeader)
    {
        if (string.IsNullOrEmpty(authHeader)) return string.Empty;
        var parts = authHeader.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 ? parts[0] : "[redacted]";
    }
}
