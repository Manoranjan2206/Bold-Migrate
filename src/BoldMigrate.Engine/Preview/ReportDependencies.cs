using System.Xml;
using System.Xml.Linq;

namespace BoldMigrate.Engine.Preview;

/// <summary>
/// The shared items a report definition depends on, read straight from its RDL.
/// </summary>
public sealed class ReportDependencies
{
    public sealed record DataSourceUse(string Name, string? SharedReference);
    public sealed record DataSetUse(string Name, string SharedReference);

    public List<DataSourceUse> DataSources { get; } = new();
    public List<DataSetUse> SharedDataSets { get; } = new();
    public List<string> Subreports { get; } = new();

    public static ReportDependencies Read(byte[] rdl)
    {
        var result = new ReportDependencies();
        XDocument doc;
        try
        {
            using var stream = new MemoryStream(rdl);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
            doc = XDocument.Load(reader);
        }
        catch (XmlException)
        {
            return result;
        }

        foreach (var ds in doc.Descendants().Where(e => e.Name.LocalName == "DataSource"))
        {
            var name = ds.Attribute("Name")?.Value ?? string.Empty;
            var reference = ds.Elements().FirstOrDefault(e => e.Name.LocalName == "DataSourceReference")?.Value.Trim();
            result.DataSources.Add(new DataSourceUse(name, string.IsNullOrEmpty(reference) ? null : reference));
        }

        foreach (var dataSet in doc.Descendants().Where(e => e.Name.LocalName == "DataSet"))
        {
            var reference = dataSet.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "SharedDataSetReference")?.Value.Trim();
            if (!string.IsNullOrEmpty(reference))
            {
                result.SharedDataSets.Add(new DataSetUse(dataSet.Attribute("Name")?.Value ?? string.Empty, reference));
            }
        }

        foreach (var sub in doc.Descendants().Where(e => e.Name.LocalName == "Subreport"))
        {
            var name = sub.Elements().FirstOrDefault(e => e.Name.LocalName == "ReportName")?.Value.Trim();
            if (!string.IsNullOrEmpty(name)) result.Subreports.Add(name);
        }

        return result;
    }

    /// <summary>
    /// The data source reference inside a shared dataset (.rsd), if any.
    /// </summary>
    public static string? ReadDataSetDataSourceReference(byte[] rsd)
    {
        try
        {
            using var stream = new MemoryStream(rsd);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
            var doc = XDocument.Load(reader);
            var value = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "DataSourceReference")?.Value.Trim();
            return string.IsNullOrEmpty(value) ? null : value;
        }
        catch (XmlException)
        {
            return null;
        }
    }

    /// <summary>
    /// True when the bytes are a well-formed RDL report definition.
    /// </summary>
    public static bool IsReportDefinition(byte[] content)
    {
        try
        {
            using var stream = new MemoryStream(content);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
            var doc = XDocument.Load(reader);
            return doc.Root?.Name.LocalName == "Report";
        }
        catch (XmlException)
        {
            return false;
        }
    }
}
