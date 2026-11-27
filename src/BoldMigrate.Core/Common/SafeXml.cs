using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace BoldMigrate.Core.Common;

/// <summary>
/// Single entry point for parsing untrusted XML (RDL, RSD, RSDS, RDS and SOAP payloads).
///
/// Every reader here sets <see cref="DtdProcessing.Prohibit"/> and a null <see cref="XmlResolver"/>,
/// which is the repository's XXE invariant. Plain <c>XDocument.Load</c>/<c>Parse</c> do not: they
/// permit inline DTDs, which leaves an entity-expansion denial-of-service open. Use these helpers
/// instead of calling XDocument directly so the invariant holds at every parse site.
/// </summary>
public static class SafeXml
{
    public static XmlReaderSettings CreateSettings() => new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreProcessingInstructions = false,
        CloseInput = false
    };

    public static XDocument Parse(string xml, LoadOptions options = LoadOptions.None)
    {
        using var stringReader = new StringReader(xml);
        using var reader = XmlReader.Create(stringReader, CreateSettings());
        return XDocument.Load(reader, options);
    }

    public static XDocument Load(Stream stream, LoadOptions options = LoadOptions.None)
    {
        using var reader = XmlReader.Create(stream, CreateSettings());
        return XDocument.Load(reader, options);
    }

    public static XDocument LoadFile(string path, LoadOptions options = LoadOptions.None)
    {
        using var fileStream = File.OpenRead(path);
        using var reader = XmlReader.Create(fileStream, CreateSettings());
        return XDocument.Load(reader, options);
    }

    public static XDocument LoadBytes(byte[] bytes, LoadOptions options = LoadOptions.None)
    {
        using var memory = new MemoryStream(bytes, writable: false);
        return Load(memory, options);
    }

    public static XDocument ParseUtf8(byte[] bytes, LoadOptions options = LoadOptions.None)
    {
        return Parse(Encoding.UTF8.GetString(bytes), options);
    }
}
