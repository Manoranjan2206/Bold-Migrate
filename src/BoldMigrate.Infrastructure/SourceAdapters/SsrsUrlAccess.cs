namespace BoldMigrate.Infrastructure.SourceAdapters;

internal static class SsrsUrlAccess
{
    /// <summary>
    /// Builds the query-string tail for SSRS URL access. Command keys ("rs:", "rc:", "dsu:", "dsp:")
    /// must keep their literal colon; SSRS ignores them when the colon is percent-encoded.
    /// </summary>
    public static string BuildQuery(IDictionary<string, string> parameters) =>
        string.Join("&", parameters.Select(kv => $"{EscapeKey(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));

    private static string EscapeKey(string key)
    {
        var colon = key.IndexOf(':');
        if (colon == 2 || colon == 3)
        {
            var prefix = key[..colon];
            if (prefix is "rs" or "rc" or "dsu" or "dsp")
                return prefix + ":" + Uri.EscapeDataString(key[(colon + 1)..]);
        }
        return Uri.EscapeDataString(key);
    }
}
