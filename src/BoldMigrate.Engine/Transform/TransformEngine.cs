using System.Text;
using System.Xml;
using System.Xml.Linq;
using System.Xml.XPath;
using BoldMigrate.Core.Enums;
using BoldMigrate.Core.Models;

namespace BoldMigrate.Engine.Transform;

public class TransformResult
{
    public bool Success { get; set; }
    public byte[] RewrittenBytes { get; set; } = Array.Empty<byte>();
    public string SchemaVersion { get; set; } = "2016";
    public ReportBucket Bucket { get; set; } = ReportBucket.Clean;
    public string? BlockReasonCode { get; set; }
    public string? Message { get; set; }
    public int ParameterCount { get; set; }
    public List<string> DataSourceNames { get; set; } = new();
    public List<string> SharedDataSetReferences { get; set; } = new();
    public List<TransformAction> Actions { get; set; } = new();
    public List<string> FeatureTags { get; set; } = new();
}

public class TransformEngine
{
    private readonly CompatibilityRulesLoader _rulesLoader;

    public TransformEngine(CompatibilityRulesLoader rulesLoader)
    {
        _rulesLoader = rulesLoader;
    }

    public string DetectSchemaVersion(XDocument doc)
    {
        var rootNs = doc.Root?.Name.NamespaceName ?? "";
        if (rootNs.Contains("2008/01")) return "2008";
        if (rootNs.Contains("2010/01")) return "2010";
        if (rootNs.Contains("2016/01")) return "2016";
        return "2016";
    }

    public TransformResult TransformAndClassify(
        Guid reportId,
        byte[] rdlBytes,
        IDictionary<string, string>? dataSourceMappings = null,
        IDictionary<string, string>? dataSetMappings = null)
    {
        var result = new TransformResult();
        var actions = new List<TransformAction>();
        var attempt = 1;

        // Strip BOM if present
        var cleanBytes = rdlBytes;
        if (rdlBytes.Length >= 3 && rdlBytes[0] == 0xEF && rdlBytes[1] == 0xBB && rdlBytes[2] == 0xBF)
        {
            cleanBytes = new byte[rdlBytes.Length - 3];
            Array.Copy(rdlBytes, 3, cleanBytes, 0, cleanBytes.Length);
            actions.Add(new TransformAction
            {
                Id = Guid.NewGuid(),
                ReportId = reportId,
                ActionCode = "TR-STRIP-BOM",
                Before = "UTF-8 BOM present",
                After = "BOM stripped",
                AttemptNumber = attempt
            });
        }

        XDocument doc;
        try
        {
            using var ms = new MemoryStream(cleanBytes);
            using var reader = XmlReader.Create(ms, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
            doc = XDocument.Load(reader);
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Bucket = ReportBucket.Blocked;
            result.BlockReasonCode = "XFRM-001";
            result.Message = $"XML parse error: {ex.Message}";
            return result;
        }

        var schemaVersion = DetectSchemaVersion(doc);
        result.SchemaVersion = schemaVersion;
        result.ParameterCount = doc.Descendants().Count(e => e.Name.LocalName == "ReportParameter");
        result.DataSourceNames = doc.Descendants()
            .Where(e => e.Name.LocalName == "DataSource")
            .Select(e => e.Attribute("Name")?.Value)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        result.SharedDataSetReferences = doc.Descendants()
            .Where(e => e.Name.LocalName == "SharedDataSetReference")
            .Select(e => e.Value.Trim())
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        XNamespace defaultNs = doc.Root?.Name.Namespace ?? XNamespace.None;

        // Apply rewrite rules
        // TR-REBIND-DS: Rebind DataSourceReference
        var dsRefs = doc.Descendants().Where(e => e.Name.LocalName == "DataSourceReference").ToList();
        foreach (var dsRef in dsRefs)
        {
            var oldRef = dsRef.Value;
            var newId = dataSourceMappings != null && dataSourceMappings.TryGetValue(oldRef, out var mapped)
                ? mapped
                : oldRef;

            if (newId != oldRef)
            {
                dsRef.Value = newId;
                actions.Add(new TransformAction
                {
                    Id = Guid.NewGuid(),
                    ReportId = reportId,
                    ActionCode = "TR-REBIND-DS",
                    Before = oldRef,
                    After = newId,
                    AttemptNumber = attempt
                });
            }
        }

        // TR-MAP-DSET: Map SharedDataSet references
        var dsetRefs = doc.Descendants().Where(e => e.Name.LocalName == "DataSetReference").ToList();
        foreach (var dsRef in dsetRefs)
        {
            var oldRef = dsRef.Value;
            if (dataSetMappings != null && dataSetMappings.TryGetValue(oldRef, out var mapped))
            {
                dsRef.Value = mapped;
                actions.Add(new TransformAction
                {
                    Id = Guid.NewGuid(),
                    ReportId = reportId,
                    ActionCode = "TR-MAP-DSET",
                    Before = oldRef,
                    After = mapped,
                    AttemptNumber = attempt
                });
            }
        }

        // TR-PATH-SUB: Subreport paths
        var subreports = doc.Descendants().Where(e => e.Name.LocalName == "Subreport").ToList();
        foreach (var sub in subreports)
        {
            var reportNameElem = sub.Elements().FirstOrDefault(e => e.Name.LocalName == "ReportName");
            if (reportNameElem != null && reportNameElem.Value.Contains('/'))
            {
                var oldVal = reportNameElem.Value;
                var newVal = oldVal.TrimStart('/');
                reportNameElem.Value = newVal;
                actions.Add(new TransformAction
                {
                    Id = Guid.NewGuid(),
                    ReportId = reportId,
                    ActionCode = "TR-PATH-SUB",
                    Before = oldVal,
                    After = newVal,
                    AttemptNumber = attempt
                });
            }
        }

        // TR-PATH-DRILL: Drillthrough ReportName
        var drillthroughs = doc.Descendants().Where(e => e.Name.LocalName == "Drillthrough").ToList();
        foreach (var drill in drillthroughs)
        {
            var reportNameElem = drill.Elements().FirstOrDefault(e => e.Name.LocalName == "ReportName");
            if (reportNameElem != null && reportNameElem.Value.Contains('/'))
            {
                var oldVal = reportNameElem.Value;
                var newVal = oldVal.TrimStart('/');
                reportNameElem.Value = newVal;
                actions.Add(new TransformAction
                {
                    Id = Guid.NewGuid(),
                    ReportId = reportId,
                    ActionCode = "TR-PATH-DRILL",
                    Before = oldVal,
                    After = newVal,
                    AttemptNumber = attempt
                });
            }
        }

        // TR-SCHEMA-UP: Upgrade RDL 2008 or 2010 to 2016 if required
        if (schemaVersion is "2008" or "2010")
        {
            XNamespace targetNs = "http://schemas.microsoft.com/sqlserver/reporting/2016/01/reportdefinition";
            if (doc.Root != null)
            {
                doc.Root.Name = targetNs + doc.Root.Name.LocalName;
                foreach (var el in doc.Descendants())
                {
                    el.Name = targetNs + el.Name.LocalName;
                }
                actions.Add(new TransformAction
                {
                    Id = Guid.NewGuid(),
                    ReportId = reportId,
                    ActionCode = "TR-SCHEMA-UP",
                    Before = $"RDL {schemaVersion}",
                    After = "RDL 2016",
                    AttemptNumber = attempt
                });
                result.SchemaVersion = "2016";
            }
        }

        // Classification against compatibility rules & feature tag extraction
        var rulesFile = _rulesLoader.LoadRules();
        var bucket = ReportBucket.Clean;
        string? blockCode = null;
        string? userMessage = null;
        var featureTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rule in rulesFile.Rules)
        {
            if (string.IsNullOrWhiteSpace(rule.XPath)) continue;

            try
            {
                var matches = doc.XPathSelectElements(rule.XPath);
                if (matches.Any())
                {
                    // Map rule code to friendly informational tag
                    var tag = rule.Code switch
                    {
                        "CF-ASSEMBLY" => "CustomAssembly",
                        "CF-SUBREPORT" => "Subreport",
                        "CF-GAUGE" => "Gauge",
                        "CF-MAP" => "Map",
                        "CF-LOOKUP" => "Lookup",
                        "CF-CODE-NET" or "CF-CODE-SIMPLE" => "CustomCode",
                        "CF-RICHTEXT" => "RichText",
                        "CF-INTERACTIVE-SORT" => "InteractiveSort",
                        "CF-SHARED-DS" => "SharedDataSource",
                        "CF-SHARED-DSET" => "SharedDataSet",
                        "CF-DOCMAP" => "DocumentMap",
                        _ => null
                    };
                    if (tag != null) featureTags.Add(tag);

                    // Blocked is fatal; NeedsReview tags items requiring manual attention (e.g. custom assemblies); Clean remains clean
                    if (rule.Bucket == ReportBucket.Blocked && bucket != ReportBucket.Blocked)
                    {
                        bucket = ReportBucket.Blocked;
                        blockCode = rule.Code;
                        userMessage = rule.Message;
                    }
                    else if (rule.Bucket == ReportBucket.NeedsReview && bucket != ReportBucket.Blocked)
                    {
                        bucket = ReportBucket.NeedsReview;
                        blockCode = rule.Code;
                        userMessage = rule.Message;
                    }
                    else if (rule.Bucket == ReportBucket.AutoFixed && bucket == ReportBucket.Clean)
                    {
                        bucket = ReportBucket.AutoFixed;
                        userMessage = rule.Message;
                    }
                }
            }
            catch
            {
                // XPath evaluation fallback
            }
        }

        // Also detect charts as an informational tag
        try
        {
            if (doc.XPathSelectElements("//*[local-name()='Chart']").Any())
            {
                featureTags.Add("Chart");
            }
        }
        catch
        {
        }

        // Serialize updated document to bytes
        using var outMs = new MemoryStream();
        doc.Save(outMs);

        result.Success = true;
        result.RewrittenBytes = outMs.ToArray();
        result.Bucket = actions.Count > 0 && bucket == ReportBucket.Clean ? ReportBucket.AutoFixed : bucket;
        result.BlockReasonCode = blockCode;
        result.Message = userMessage;
        result.Actions = actions;
        result.FeatureTags = featureTags.OrderBy(t => t).ToList();

        return result;
    }
}
