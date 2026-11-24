using BoldMigrate.Core.Enums;
using BoldMigrate.Core.Models;

namespace BoldMigrate.Engine.Preview;

/// <summary>
/// Read-only view of one run's staged catalog, addressed by the original SSRS paths
/// (e.g. <c>/Sales/Monthly Summary</c>). The local report viewer and designer resolve every
/// report, shared data source, shared dataset and resource through this lookup, so a path
/// coming from the browser is only ever matched against catalog entries and never joined
/// into a file system path.
/// </summary>
public sealed class StagedCatalog
{
    private static readonly string[] KnownExtensions = { ".rdl", ".rdlc", ".rds", ".rsds", ".rsd" };

    private readonly Dictionary<(ItemType, string), CatalogItem> _byPath = new();

    public Guid RunId { get; }

    public IReadOnlyList<CatalogItem> Items { get; }

    public StagedCatalog(Guid runId, IEnumerable<CatalogItem> items)
    {
        RunId = runId;
        Items = items.Where(i => i.RunId == runId).ToList();
        foreach (var item in Items)
        {
            // The first item wins if discovery ever recorded the same path twice.
            _byPath.TryAdd((item.ItemType, Key(item.SourcePath)), item);
        }
    }

    /// <summary>
    /// Normalises an SSRS catalog path: forward slashes, a single leading slash, no trailing
    /// slash, and "." / ".." segments collapsed. ".." never climbs above the root.
    /// </summary>
    public static string NormalizePath(string? path)
    {
        var segments = new List<string>();
        foreach (var raw in (path ?? string.Empty).Replace('\\', '/').Split('/'))
        {
            var segment = raw.Trim();
            if (segment.Length == 0 || segment == ".") continue;
            if (segment == "..")
            {
                if (segments.Count > 0) segments.RemoveAt(segments.Count - 1);
                continue;
            }
            segments.Add(segment);
        }
        return "/" + string.Join('/', segments);
    }

    public static string ParentOf(string? path)
    {
        var normalized = NormalizePath(path);
        var idx = normalized.LastIndexOf('/');
        return idx <= 0 ? "/" : normalized[..idx];
    }

    public CatalogItem? FindByPath(string? path, ItemType type)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (_byPath.TryGetValue((type, Key(path)), out var item)) return item;

        // Designer/viewer requests sometimes carry the file extension (Report1.rdl).
        var withoutExt = StripKnownExtension(path);
        return withoutExt != path && _byPath.TryGetValue((type, Key(withoutExt)), out item) ? item : null;
    }

    /// <summary>
    /// Resolves a reference the way SSRS does: absolute paths from the root, relative paths
    /// against <paramref name="contextFolder"/> (the referencing item's folder). If neither
    /// matches, a bare name is accepted when exactly one item of that type carries it, which
    /// covers references rewritten to a plain name by the designer or the transform step.
    /// </summary>
    public CatalogItem? ResolveReference(string? reference, string? contextFolder, ItemType type)
    {
        if (string.IsNullOrWhiteSpace(reference)) return null;
        var trimmed = reference.Trim();

        if (trimmed.StartsWith('/') || trimmed.StartsWith('\\'))
        {
            var absolute = FindByPath(trimmed, type);
            if (absolute != null) return absolute;
        }
        else
        {
            var relative = FindByPath(NormalizePath((contextFolder ?? "/") + "/" + trimmed), type);
            if (relative != null) return relative;

            // Some exports omit the leading slash on what is really an absolute path.
            var rooted = FindByPath(trimmed, type);
            if (rooted != null) return rooted;
        }

        var name = StripKnownExtension(NormalizePath(trimmed)).Split('/').Last();
        var byName = Items
            .Where(i => i.ItemType == type && string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToList();
        return byName.Count == 1 ? byName[0] : null;
    }

    public IEnumerable<CatalogItem> OfType(ItemType type) => Items.Where(i => i.ItemType == type);

    public IEnumerable<CatalogItem> ChildrenOf(string? folder, ItemType type)
    {
        var parent = NormalizePath(folder);
        return OfType(type).Where(i => string.Equals(ParentOf(i.SourcePath), parent, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// True when the item has a staged file on disk that can be served.
    /// </summary>
    public static bool IsStaged(CatalogItem item) =>
        !string.IsNullOrEmpty(item.StagedFilePath) && File.Exists(item.StagedFilePath);

    private static string Key(string path) => NormalizePath(path).ToUpperInvariant();

    private static string StripKnownExtension(string path)
    {
        foreach (var ext in KnownExtensions)
        {
            if (path.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) return path[..^ext.Length];
        }
        return path;
    }
}
