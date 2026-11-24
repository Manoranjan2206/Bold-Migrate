using System.Net;
using System.Xml;
using System.Xml.Linq;

namespace BoldMigrate.Engine.Preview;

/// <summary>
/// Connection settings read from a staged shared data source. Accepts every format SSRS
/// produces: the report-server <c>DataSourceDefinition</c> (.rsds, returned by both the SOAP
/// and REST APIs) and the Visual Studio project <c>RptDataSource</c> (.rds).
/// </summary>
public sealed class SharedDataSourceDefinition
{
    public const string DefaultPrompt = "Enter a user name and password to access the data source:";

    public string Extension { get; set; } = "SQL";
    public string ConnectString { get; set; } = string.Empty;

    /// <summary>Prompt, Store, Integrated or None — the SSRS / Bold Reports names.</summary>
    public string CredentialRetrieval { get; set; } = "Prompt";

    public bool WindowsCredentials { get; set; }
    public bool ImpersonateUser { get; set; }
    public string? UserName { get; set; }
    public string? Password { get; set; }
    public string? Prompt { get; set; }
    public bool Enabled { get; set; } = true;

    // SSRS extension names that Bold Reports registers under a different name.
    private static readonly Dictionary<string, string> ExtensionAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SQLAZURE"] = "SQL",
        ["SQLPDW"] = "SQL",
        ["OLEDB-MD"] = "SSAS",
    };

    /// <summary>
    /// Parses staged data source XML. Returns null when the content is not a data source definition.
    /// </summary>
    public static SharedDataSourceDefinition? Parse(byte[] content)
    {
        XDocument doc;
        try
        {
            using var stream = new MemoryStream(content);
            // DTDs are never legitimate in a data source definition; refusing them blocks XXE.
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
            doc = XDocument.Load(reader);
        }
        catch (XmlException)
        {
            return null;
        }

        var root = doc.Root;
        if (root == null) return null;

        // .rds wraps the settings in <ConnectionProperties>; .rsds has them on the root.
        var props = root.Name.LocalName switch
        {
            "RptDataSource" => Child(root, "ConnectionProperties"),
            "DataSourceDefinition" => root,
            _ => null
        };
        if (props == null) return null;

        var extension = Value(props, "Extension") ?? "SQL";
        var def = new SharedDataSourceDefinition
        {
            Extension = ExtensionAliases.TryGetValue(extension, out var alias) ? alias : extension,
            ConnectString = Value(props, "ConnectString") ?? string.Empty,
            WindowsCredentials = Flag(props, "WindowsCredentials"),
            ImpersonateUser = Flag(props, "ImpersonateUser"),
            UserName = Value(props, "UserName"),
            Prompt = Value(props, "Prompt"),
            Enabled = Value(props, "Enabled") is not { } enabled || !string.Equals(enabled, "false", StringComparison.OrdinalIgnoreCase),
        };

        var retrieval = Value(props, "CredentialRetrieval");
        if (Flag(props, "IntegratedSecurity"))
        {
            def.CredentialRetrieval = "Integrated";
        }
        else if (retrieval != null && new[] { "Prompt", "Store", "Integrated", "None" }
                     .FirstOrDefault(r => r.Equals(retrieval, StringComparison.OrdinalIgnoreCase)) is { } known)
        {
            def.CredentialRetrieval = known;
        }
        else
        {
            // .rds files without IntegratedSecurity ask for credentials at run time in SSRS too.
            def.CredentialRetrieval = "Prompt";
        }

        return def;
    }

    /// <summary>
    /// Decides how the local viewer should authenticate. Credentials entered in Step 1 always
    /// win. Otherwise Integrated and None are kept as defined, and anything that needs a stored
    /// password SSRS never exported falls back to Prompt, so the viewer asks at run time.
    /// </summary>
    public SharedDataSourceDefinition WithCredentials(NetworkCredential? stored)
    {
        var effective = (SharedDataSourceDefinition)MemberwiseClone();
        effective.Password = null;

        if (stored != null && !string.IsNullOrEmpty(stored.UserName))
        {
            effective.CredentialRetrieval = "Store";
            effective.UserName = string.IsNullOrEmpty(stored.Domain) ? stored.UserName : $"{stored.Domain}\\{stored.UserName}";
            effective.Password = stored.Password;
            return effective;
        }

        if (CredentialRetrieval is "Integrated" or "None") return effective;

        effective.CredentialRetrieval = "Prompt";
        effective.Prompt = string.IsNullOrWhiteSpace(Prompt) ? DefaultPrompt : Prompt;
        return effective;
    }

    private static XElement? Child(XElement parent, string localName) =>
        parent.Elements().FirstOrDefault(e => e.Name.LocalName == localName);

    private static string? Value(XElement parent, string localName)
    {
        var value = Child(parent, localName)?.Value.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static bool Flag(XElement parent, string localName) =>
        string.Equals(Value(parent, localName), "true", StringComparison.OrdinalIgnoreCase);
}
