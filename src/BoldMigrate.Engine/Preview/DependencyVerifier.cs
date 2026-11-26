using System.Xml;
using System.Xml.Linq;
using BoldMigrate.Core.Enums;
using BoldMigrate.Core.Models;

namespace BoldMigrate.Engine.Preview;

public sealed record LinkCheckDetail(
    string LinkType,
    string Name,
    string Reference,
    string? ResolvedPath,
    bool Resolved,
    bool IsCycle = false,
    string? Issue = null);

public sealed record ReportLinkAudit(
    Guid ReportId,
    string Name,
    string Path,
    bool Staged,
    int BrokenCount,
    bool HasCycle,
    List<LinkCheckDetail> Links);

public sealed record DependencyVerificationSummary(
    int TotalReports,
    int HealthyReports,
    int ReportsWithIssues,
    int BrokenDataSources,
    int BrokenDataSets,
    int BrokenSubreports,
    int CyclesDetected,
    List<ReportLinkAudit> Reports,
    List<string> AvailableDataSources,
    List<string> AvailableDataSets,
    List<string> AvailableReports);

/// <summary>
/// Verifies all staged report references (shared data sources, shared datasets, chained dataset
/// data sources, and subreports including cycles) and supports safe XML rebinding.
/// </summary>
public static class DependencyVerifier
{
    public static DependencyVerificationSummary VerifyCatalog(Guid runId, IEnumerable<CatalogItem> catalogItems)
    {
        var catalog = new StagedCatalog(runId, catalogItems);
        var reports = catalog.OfType(ItemType.Report)
            .OrderBy(r => r.SourcePath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Precompute subreport adjacency graph for cycle detection
        var subreportEdges = new Dictionary<Guid, List<CatalogItem>>();
        var depsByReport = new Dictionary<Guid, ReportDependencies>();

        foreach (var report in reports)
        {
            var folder = StagedCatalog.ParentOf(report.SourcePath);
            var deps = StagedCatalog.IsStaged(report)
                ? ReportDependencies.Read(File.ReadAllBytes(report.StagedFilePath!))
                : new ReportDependencies();
            depsByReport[report.Id] = deps;

            var targets = new List<CatalogItem>();
            foreach (var subRef in deps.Subreports)
            {
                var resolved = catalog.ResolveReference(subRef, folder, ItemType.Report);
                if (resolved != null) targets.Add(resolved);
            }
            subreportEdges[report.Id] = targets;
        }

        var audits = new List<ReportLinkAudit>(reports.Count);
        var brokenDs = 0;
        var brokenDset = 0;
        var brokenSub = 0;
        var cycles = 0;

        foreach (var report in reports)
        {
            var folder = StagedCatalog.ParentOf(report.SourcePath);
            var staged = StagedCatalog.IsStaged(report);
            var deps = depsByReport[report.Id];
            var links = new List<LinkCheckDetail>();

            if (!staged)
            {
                audits.Add(new ReportLinkAudit(
                    report.Id,
                    report.Name,
                    StagedCatalog.NormalizePath(report.SourcePath),
                    false,
                    1,
                    false,
                    [new LinkCheckDetail("Report", report.Name, report.SourcePath, null, false, false, "Staged RDL file is missing on disk.")]));
                continue;
            }

            foreach (var ds in deps.DataSources)
            {
                if (string.IsNullOrWhiteSpace(ds.SharedReference)) continue;
                var shared = catalog.ResolveReference(ds.SharedReference, folder, ItemType.DataSource);
                var ok = shared != null && StagedCatalog.IsStaged(shared);
                if (!ok) brokenDs++;
                links.Add(new LinkCheckDetail(
                    "DataSource",
                    ds.Name,
                    ds.SharedReference,
                    shared != null ? StagedCatalog.NormalizePath(shared.SourcePath) : null,
                    ok,
                    false,
                    ok ? null : "Shared DataSource target not found in staged catalog."));
            }

            foreach (var dset in deps.SharedDataSets)
            {
                var shared = catalog.ResolveReference(dset.SharedReference, folder, ItemType.DataSet);
                var ok = shared != null && StagedCatalog.IsStaged(shared);
                string? issue = ok ? null : "Shared DataSet target not found in staged catalog.";

                if (ok && shared != null)
                {
                    var dsetBytes = File.ReadAllBytes(shared.StagedFilePath!);
                    var chainedDsRef = ReportDependencies.ReadDataSetDataSourceReference(dsetBytes);
                    if (!string.IsNullOrWhiteSpace(chainedDsRef))
                    {
                        var chainedDs = catalog.ResolveReference(chainedDsRef, StagedCatalog.ParentOf(shared.SourcePath), ItemType.DataSource);
                        if (chainedDs == null || !StagedCatalog.IsStaged(chainedDs))
                        {
                            ok = false;
                            issue = $"Dataset's DataSource '{chainedDsRef}' is missing from staged catalog.";
                        }
                    }
                }

                if (!ok) brokenDset++;
                links.Add(new LinkCheckDetail(
                    "DataSet",
                    dset.Name,
                    dset.SharedReference,
                    shared != null ? StagedCatalog.NormalizePath(shared.SourcePath) : null,
                    ok,
                    false,
                    issue));
            }

            var reportHasCycle = false;
            foreach (var subRef in deps.Subreports)
            {
                var target = catalog.ResolveReference(subRef, folder, ItemType.Report);
                var resolved = target != null && StagedCatalog.IsStaged(target);
                var isCycle = false;
                string? issue = resolved ? null : "Subreport target report not found in staged catalog.";

                if (target != null)
                {
                    isCycle = target.Id == report.Id || CanReach(target.Id, report.Id, subreportEdges, new HashSet<Guid>());
                    if (isCycle)
                    {
                        reportHasCycle = true;
                        cycles++;
                        issue = target.Id == report.Id
                            ? "Self-referencing subreport cycle detected."
                            : "Circular subreport reference chain detected.";
                    }
                }

                if (!resolved) brokenSub++;
                links.Add(new LinkCheckDetail(
                    "Subreport",
                    subRef,
                    subRef,
                    target != null ? StagedCatalog.NormalizePath(target.SourcePath) : null,
                    resolved && !isCycle,
                    isCycle,
                    issue));
            }

            var brokenCount = links.Count(l => !l.Resolved);
            audits.Add(new ReportLinkAudit(
                report.Id,
                report.Name,
                StagedCatalog.NormalizePath(report.SourcePath),
                true,
                brokenCount,
                reportHasCycle,
                links));
        }

        var reportsWithIssues = audits.Count(a => a.BrokenCount > 0 || a.HasCycle);
        return new DependencyVerificationSummary(
            reports.Count,
            reports.Count - reportsWithIssues,
            reportsWithIssues,
            brokenDs,
            brokenDset,
            brokenSub,
            cycles,
            audits,
            catalog.OfType(ItemType.DataSource).Select(i => StagedCatalog.NormalizePath(i.SourcePath)).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList(),
            catalog.OfType(ItemType.DataSet).Select(i => StagedCatalog.NormalizePath(i.SourcePath)).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList(),
            reports.Select(i => StagedCatalog.NormalizePath(i.SourcePath)).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList());
    }

    private static bool CanReach(Guid current, Guid target, Dictionary<Guid, List<CatalogItem>> edges, HashSet<Guid> visited)
    {
        if (!visited.Add(current)) return false;
        if (!edges.TryGetValue(current, out var next)) return false;
        foreach (var child in next)
        {
            if (child.Id == target || CanReach(child.Id, target, edges, visited))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Updates a DataSourceReference, SharedDataSetReference, or Subreport/ReportName in an RDL XML document.
    /// </summary>
    public static (bool Updated, byte[] UpdatedBytes) RebindReferenceInRdl(
        byte[] rdlBytes,
        string referenceType,
        string oldReference,
        string newReference)
    {
        if (string.IsNullOrWhiteSpace(newReference)) return (false, rdlBytes);

        using var stream = new MemoryStream(rdlBytes);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
        var doc = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        var updated = false;
        var oldTrim = (oldReference ?? string.Empty).Trim();
        var newTrim = newReference.Trim();

        IEnumerable<XElement> candidates = referenceType.ToLowerInvariant() switch
        {
            "datasource" => doc.Descendants().Where(e => e.Name.LocalName == "DataSourceReference"),
            "dataset" => doc.Descendants().Where(e => e.Name.LocalName == "SharedDataSetReference"),
            "subreport" => doc.Descendants()
                .Where(e => e.Name.LocalName == "Subreport")
                .SelectMany(s => s.Elements().Where(e => e.Name.LocalName == "ReportName")),
            _ => Array.Empty<XElement>()
        };

        foreach (var el in candidates)
        {
            if (string.IsNullOrEmpty(oldTrim) || string.Equals(el.Value.Trim(), oldTrim, StringComparison.OrdinalIgnoreCase))
            {
                el.Value = newTrim;
                updated = true;
            }
        }

        if (!updated) return (false, rdlBytes);

        using var outMs = new MemoryStream();
        doc.Save(outMs);
        return (true, outMs.ToArray());
    }
}
