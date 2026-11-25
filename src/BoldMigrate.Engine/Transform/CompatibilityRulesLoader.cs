using System.Text.Json;

namespace BoldMigrate.Engine.Transform;

public class CompatibilityRulesLoader
{
    private RulesFileFormat? _cachedRules;
    private DateTime _lastLoadedUtc = DateTime.MinValue;
    private readonly string _rulesFilePath;

    public CompatibilityRulesLoader(string? rulesFilePath = null)
    {
        _rulesFilePath = ResolveRulesFilePath(rulesFilePath);
    }

    public static string ResolveRulesFilePath(string? configuredPath = null)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
            return configuredPath;

        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "data", "compatibility-rules.json"),
            Path.Combine(Directory.GetCurrentDirectory(), "data", "compatibility-rules.json"),
            Path.Combine(Directory.GetCurrentDirectory(), "..", "data", "compatibility-rules.json"),
            Path.Combine(Directory.GetCurrentDirectory(), "..", "..", "data", "compatibility-rules.json")
        };

        // Also search upwards from current directory and AppContext.BaseDirectory
        var dirsToProbe = new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory };
        foreach (var dir in dirsToProbe)
        {
            var curr = dir;
            for (int i = 0; i < 5; i++)
            {
                var candidate = Path.Combine(curr, "data", "compatibility-rules.json");
                if (File.Exists(candidate)) return candidate;
                var parent = Directory.GetParent(curr);
                if (parent == null) break;
                curr = parent.FullName;
            }
        }

        return candidates.FirstOrDefault(File.Exists) ?? Path.Combine(AppContext.BaseDirectory, "data", "compatibility-rules.json");
    }

    public RulesFileFormat LoadRules(bool forceReload = false)
    {
        if (!forceReload && _cachedRules != null && (DateTime.UtcNow - _lastLoadedUtc).TotalHours < 24)
        {
            return _cachedRules;
        }

        if (File.Exists(_rulesFilePath))
        {
            var json = File.ReadAllText(_rulesFilePath);
            var parsed = JsonSerializer.Deserialize<RulesFileFormat>(json);
            if (parsed != null)
            {
                // Validate that every rule has an article link per spec
                foreach (var rule in parsed.Rules)
                {
                    if (string.IsNullOrWhiteSpace(rule.Article))
                    {
                        throw new InvalidOperationException($"Rule '{rule.Code}' has no knowledge base article address. All rules MUST have an article link.");
                    }
                }

                _cachedRules = parsed;
                _lastLoadedUtc = DateTime.UtcNow;
                return parsed;
            }
        }

        // Fallback default rules if file is missing
        var fallback = GetFallbackRules();
        _cachedRules = fallback;
        _lastLoadedUtc = DateTime.UtcNow;
        return fallback;
    }

    public static RulesFileFormat GetFallbackRules()
    {
        return new RulesFileFormat
        {
            Version = "2026.11.1",
            Rules = new List<ClassificationRule>
            {
                new() { Code = "CF-ASSEMBLY", XPath = "//*[local-name()='CodeModule']", BucketStr = "NeedsReview", Message = "Uses custom assemblies — needs review.", Article = "https://support.boldreports.com/kb/custom-assemblies" },
                new() { Code = "CF-CODE-NET", XPath = "//*[local-name()='Code'][contains(., 'System.')]", BucketStr = "Clean", Message = "Contains embedded code that calls framework types.", Article = "https://support.boldreports.com/kb/embedded-code-net" },
                new() { Code = "CF-CODE-SIMPLE", XPath = "//*[local-name()='Code']", BucketStr = "Clean", Message = "Contains simple embedded code, carried over as is.", Article = "https://support.boldreports.com/kb/embedded-code-simple" },
                new() { Code = "CF-SUBREPORT", XPath = "//*[local-name()='Subreport']", BucketStr = "Clean", Message = "Contains a subreport.", Article = "https://support.boldreports.com/kb/subreports" },
                new() { Code = "CF-LOOKUP", XPath = "//*[local-name()='Value'][contains(., 'Lookup(') or contains(., 'LookupSet(') or contains(., 'RunningValue(') or contains(., 'Previous(') or contains(., 'Aggregate(')]", BucketStr = "Clean", Message = "Uses a scoped aggregate function.", Article = "https://support.boldreports.com/kb/scoped-aggregates" },
                new() { Code = "CF-MAP", XPath = "//*[local-name()='Map']", BucketStr = "Clean", Message = "Uses a map visual.", Article = "https://support.boldreports.com/kb/maps" },
                new() { Code = "CF-GAUGE", XPath = "//*[local-name()='GaugePanel']", BucketStr = "Clean", Message = "Uses a gauge visual.", Article = "https://support.boldreports.com/kb/gauges" },
                new() { Code = "CF-INTERACTIVE-SORT", XPath = "//*[local-name()='UserSort']", BucketStr = "Clean", Message = "Uses interactive sorting.", Article = "https://support.boldreports.com/kb/interactive-sort" },
                new() { Code = "CF-RICHTEXT", XPath = "//*[local-name()='MarkupType'][text()='HTML']", BucketStr = "Clean", Message = "Uses rich text inside a text box.", Article = "https://support.boldreports.com/kb/rich-text" },
                new() { Code = "CF-SHARED-DSET", XPath = "//*[local-name()='SharedDataSet']", BucketStr = "Clean", Message = "Uses a shared dataset.", Article = "https://support.boldreports.com/kb/shared-datasets" },
                new() { Code = "CF-SHARED-DS", XPath = "//*[local-name()='DataSourceReference']", BucketStr = "Clean", Message = "Uses a shared data source.", Article = "https://support.boldreports.com/kb/shared-datasources" },
                new() { Code = "CF-DOCMAP", XPath = "//*[local-name()='DocumentMapLabel']", BucketStr = "Clean", Message = "Uses a document map.", Article = "https://support.boldreports.com/kb/document-maps" },
                new() { Code = "CF-UNSUPPORTED-TYPE", XPath = "", BucketStr = "NeedsReview", Message = "Uses an item type that needs review.", Article = "https://support.boldreports.com/kb/unsupported-types" }
            }
        };
    }
}
