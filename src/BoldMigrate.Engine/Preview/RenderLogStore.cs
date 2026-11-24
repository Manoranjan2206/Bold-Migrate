using System.Collections.Concurrent;
using BoldMigrate.Core.Models;

namespace BoldMigrate.Engine.Preview;

public interface IRenderLogStore
{
    void AddEntry(Guid runId, string reportKey, RenderLogEntry entry);
    IReadOnlyList<RenderLogEntry> GetEntries(Guid runId, string reportKey);
    IReadOnlyDictionary<string, List<RenderLogEntry>> GetAllEntriesForRun(Guid runId);
    void Clear(Guid runId, string? reportKey = null);
}

public class InMemoryRenderLogStore : IRenderLogStore
{
    private readonly ConcurrentDictionary<(Guid RunId, string Key), ConcurrentBag<RenderLogEntry>> _logs = new();

    private static string NormalizeKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return "unknown";
        var norm = key.Trim().Replace('\\', '/');
        norm = norm.TrimStart('/');
        var extIdx = norm.LastIndexOf('.');
        if (extIdx > 0 && (norm.EndsWith(".rdl", StringComparison.OrdinalIgnoreCase) || norm.EndsWith(".rdlc", StringComparison.OrdinalIgnoreCase)))
        {
            norm = norm[..extIdx];
        }
        return norm.ToLowerInvariant();
    }

    public void AddEntry(Guid runId, string reportKey, RenderLogEntry entry)
    {
        var normKey = NormalizeKey(reportKey);
        var bag = _logs.GetOrAdd((runId, normKey), _ => new ConcurrentBag<RenderLogEntry>());
        bag.Add(entry);
    }

    public IReadOnlyList<RenderLogEntry> GetEntries(Guid runId, string reportKey)
    {
        var normKey = NormalizeKey(reportKey);
        if (_logs.TryGetValue((runId, normKey), out var bag))
        {
            return bag.OrderBy(e => e.TimestampUtc).ToList();
        }

        // Try matching by report name segment
        var leaf = normKey.Split('/').Last();
        foreach (var entry in _logs)
        {
            if (entry.Key.RunId == runId && (entry.Key.Key == leaf || entry.Key.Key.EndsWith("/" + leaf)))
            {
                return entry.Value.OrderBy(e => e.TimestampUtc).ToList();
            }
        }

        return Array.Empty<RenderLogEntry>();
    }

    public IReadOnlyDictionary<string, List<RenderLogEntry>> GetAllEntriesForRun(Guid runId)
    {
        var result = new Dictionary<string, List<RenderLogEntry>>();
        foreach (var entry in _logs)
        {
            if (entry.Key.RunId == runId)
            {
                result[entry.Key.Key] = entry.Value.OrderBy(e => e.TimestampUtc).ToList();
            }
        }
        return result;
    }

    public void Clear(Guid runId, string? reportKey = null)
    {
        if (string.IsNullOrWhiteSpace(reportKey))
        {
            foreach (var key in _logs.Keys.Where(k => k.RunId == runId).ToList())
            {
                _logs.TryRemove(key, out _);
            }
        }
        else
        {
            var normKey = NormalizeKey(reportKey);
            _logs.TryRemove((runId, normKey), out _);
        }
    }
}
