using System.Net;
using BoldMigrate.Engine.Preview;
using BoldMigrate.Infrastructure.Credentials;
using BoldReports.ServerProcessor;
using MigrationItemType = BoldMigrate.Core.Enums.ItemType;
using MigrationCatalogItem = BoldMigrate.Core.Models.CatalogItem;

namespace BoldMigrate.Api.Preview;

/// <summary>
/// Serves one migration run's staged files to the Bold Reports viewer and designer, so
/// converted reports can be previewed and edited before anything is uploaded.
///
/// Follows the external ReportingServer pattern from the Bold Reports samples, but instead
/// of fixed Report/DataSource/DataSet folders it resolves items through the run's catalog,
/// which mirrors the SSRS folder tree (e.g. <c>/Sales/Monthly Summary</c>). References are
/// resolved the way SSRS does: absolute, or relative to the referencing item's folder.
/// </summary>
public sealed class StagedReportServer : ReportingServer
{
    /// <summary>Server URL handed to Bold Reports so it uses this server; never contacted.</summary>
    public const string PlaceholderServerUrl = "staged://boldmigrate";

    private readonly StagedCatalog _catalog;
    private readonly ICredentialStore _credentials;
    private readonly Func<MigrationCatalogItem, byte[], string?> _saveReport;
    private readonly ILogger _logger;
    private readonly bool _allowUntrustedSqlCert;

    // Folder of the report currently being processed; data source, dataset and subreport
    // references inside it are relative to this folder (isolated per async execution context).
    private readonly AsyncLocal<string?> _contextFolder = new();

    // Data source of the most recently resolved shared dataset.
    private readonly AsyncLocal<MigrationCatalogItem?> _lastDataSetDataSource = new();

    private string CurrentContextFolder => _contextFolder.Value ?? "/";

    /// <param name="saveReport">
    /// Persists a designer save for a staged report. Returns null on success, otherwise the
    /// error to show in the designer.
    /// </param>
    public StagedReportServer(
        StagedCatalog catalog,
        ICredentialStore credentials,
        Func<MigrationCatalogItem, byte[], string?> saveReport,
        ILogger logger,
        bool allowUntrustedSqlCert = false)
    {
        _catalog = catalog;
        _credentials = credentials;
        _saveReport = saveReport;
        _logger = logger;
        _allowUntrustedSqlCert = allowUntrustedSqlCert;
    }

    public override Stream GetReport()
    {
        var report = ResolveReport(ReportPath);
        if (report == null)
        {
            _logger.LogWarning("Preview: report '{Path}' is not staged for run {RunId}.", ReportPath, _catalog.RunId);
            return null!;
        }

        _contextFolder.Value = StagedCatalog.ParentOf(report.SourcePath);
        var bytes = File.ReadAllBytes(report.StagedFilePath!);
        bytes = EnsureTrustServerCertificateInRdl(bytes, _allowUntrustedSqlCert);
        return new MemoryStream(bytes);
    }

    public override DataSourceDefinition GetDataSourceDefinition(string dataSource)
    {
        // While a shared dataset is expanded, Bold Reports asks for its data source with an
        // empty reference; that means the one the dataset itself points to.
        var item = string.IsNullOrWhiteSpace(dataSource)
            ? _lastDataSetDataSource.Value
            : _catalog.ResolveReference(dataSource, CurrentContextFolder, MigrationItemType.DataSource);
        var definition = item != null ? BuildDataSourceDefinition(item) : null;
        if (definition == null)
        {
            _logger.LogWarning("Preview: shared data source '{Reference}' could not be resolved from staging (run {RunId}).",
                dataSource, _catalog.RunId);
        }
        return definition!;
    }

    public override SharedDatasetinfo GetSharedDataDefinition(string path)
    {
        var item = _catalog.ResolveReference(path, CurrentContextFolder, MigrationItemType.DataSet);
        if (item == null || !StagedCatalog.IsStaged(item))
        {
            _logger.LogWarning("Preview: shared dataset '{Reference}' could not be resolved from staging (run {RunId}).",
                path, _catalog.RunId);
            return null!;
        }

        var content = File.ReadAllBytes(item.StagedFilePath!);
        var info = new SharedDatasetinfo
        {
            DataSetStream = new MemoryStream(content),
            Guid = item.Id.ToString()
        };

        // The dataset's own data source reference is relative to the dataset's folder.
        var dsReference = ReportDependencies.ReadDataSetDataSourceReference(content);
        var dsItem = _catalog.ResolveReference(dsReference, StagedCatalog.ParentOf(item.SourcePath), MigrationItemType.DataSource);
        _lastDataSetDataSource.Value = dsItem;
        if (dsItem != null)
        {
            info.DataSourceDefinition = BuildDataSourceDefinition(dsItem);
        }

        return info;
    }

    public override BoldReports.RDL.DOM.Server.ItemDefinition GetItemDefinition(string itemName)
    {
        // External images and other resources referenced by a report.
        var item = _catalog.ResolveReference(itemName, CurrentContextFolder, MigrationItemType.Resource);
        if (item == null || !StagedCatalog.IsStaged(item)) return null!;

        return new BoldReports.RDL.DOM.Server.ItemDefinition
        {
            FileContent = File.ReadAllBytes(item.StagedFilePath!)
        };
    }

    public override List<CatalogItem> GetItems(string folderName, ItemTypeEnum type, string permissionType)
    {
        var folder = StagedCatalog.NormalizePath(folderName);

        return type switch
        {
            ItemTypeEnum.Folder => ChildFolders(folder)
                .Select(f => new CatalogItem { Id = f, Name = f.Split('/').Last(), Type = ItemTypeEnum.Folder })
                .ToList(),
            ItemTypeEnum.Report => _catalog.ChildrenOf(folder, MigrationItemType.Report)
                .Where(StagedCatalog.IsStaged)
                .Select(r => ToBoldItem(r, ItemTypeEnum.Report))
                .ToList(),
            // Shared data sources and datasets are offered from anywhere in the run, as on SSRS.
            ItemTypeEnum.DataSource => _catalog.OfType(MigrationItemType.DataSource)
                .Where(StagedCatalog.IsStaged)
                .Select(d => ToBoldItem(d, ItemTypeEnum.DataSource))
                .ToList(),
            ItemTypeEnum.DataSet => _catalog.OfType(MigrationItemType.DataSet)
                .Where(StagedCatalog.IsStaged)
                .Select(d => ToBoldItem(d, ItemTypeEnum.DataSet))
                .ToList(),
            _ => new List<CatalogItem>()
        };
    }

    public override bool EditReport(byte[] reportdata)
    {
        var report = ResolveReport(ReportPath);
        if (report == null) return false;
        return _saveReport(report, reportdata) == null;
    }

    public override bool CreateReport(string reportname, string folderName, byte[] reportdata, out string exception)
    {
        // Only staged reports can be saved: a brand-new report has no source item to migrate,
        // so "Save As" under a new name is refused instead of silently creating stray files.
        var path = string.IsNullOrWhiteSpace(folderName) || reportname.Contains('/')
            ? reportname
            : StagedCatalog.NormalizePath(folderName + "/" + reportname);

        // No fallback to the opened report: "Save As" a new name must not overwrite it.
        var report = ResolveReport(path);
        if (report == null)
        {
            exception = $"'{reportname}' is not a report in this migration run. Save changes to the report you opened.";
            return false;
        }

        var error = _saveReport(report, reportdata);
        exception = error!;
        return error == null;
    }

    private MigrationCatalogItem? ResolveReport(string? path)
    {
        var report = _catalog.ResolveReference(path, CurrentContextFolder, MigrationItemType.Report);
        return report != null && StagedCatalog.IsStaged(report) ? report : null;
    }

    private DataSourceDefinition? BuildDataSourceDefinition(MigrationCatalogItem item)
    {
        if (!StagedCatalog.IsStaged(item)) return null;

        var parsed = SharedDataSourceDefinition.Parse(File.ReadAllBytes(item.StagedFilePath!));
        if (parsed == null) return null;

        var effective = parsed.WithCredentials(_credentials.GetDataSourceCredential(item.Id.ToString()));

        var connectString = effective.ConnectString;
        if (ShouldAppendTrustServerCertificate(connectString, effective.Extension, _allowUntrustedSqlCert))
        {
            connectString = connectString.TrimEnd(';') + ";TrustServerCertificate=True;";
        }

        return new DataSourceDefinition
        {
            Name = item.Name,
            Guid = item.Id.ToString(),
            Extension = effective.Extension,
            ConnectString = connectString,
            CredentialRetrieval = Enum.Parse<CredentialRetrievalEnum>(effective.CredentialRetrieval),
            UserName = effective.UserName,
            Password = effective.Password,
            Prompt = effective.Prompt,
            WindowsCredentials = effective.WindowsCredentials,
            ImpersonateUser = effective.ImpersonateUser,
            Enabled = effective.Enabled,
            EnabledSpecified = true
        };
    }

    private static bool ShouldAppendTrustServerCertificate(string? connectString, string? extension, bool allowUntrusted)
    {
        if (string.IsNullOrWhiteSpace(connectString)) return false;
        var isSql = string.Equals(extension, "SQL", StringComparison.OrdinalIgnoreCase) ||
                    connectString.Contains("Server=", StringComparison.OrdinalIgnoreCase) ||
                    connectString.Contains("Data Source=", StringComparison.OrdinalIgnoreCase);
        if (!isSql || connectString.Contains("TrustServerCertificate=", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return allowUntrusted || IsLocalOrInternalSqlHost(connectString);
    }

    private static bool IsLocalOrInternalSqlHost(string connectString)
    {
        try
        {
            var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(connectString);
            var server = (builder.DataSource ?? string.Empty).Split(',')[0].Split('\\')[0].Trim();
            return server.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                   server.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
                   server.Equals("(local)", StringComparison.OrdinalIgnoreCase) ||
                   server.Equals(".", StringComparison.OrdinalIgnoreCase) ||
                   server.Equals("host.docker.internal", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static byte[] EnsureTrustServerCertificateInRdl(byte[] rdlBytes, bool allowUntrusted)
    {
        try
        {
            using var ms = new MemoryStream(rdlBytes);
            var readerSettings = new System.Xml.XmlReaderSettings
            {
                DtdProcessing = System.Xml.DtdProcessing.Prohibit,
                XmlResolver = null
            };
            using var reader = System.Xml.XmlReader.Create(ms, readerSettings);
            var doc = System.Xml.Linq.XDocument.Load(reader);
            var modified = false;
            foreach (var elem in doc.Descendants().Where(e => e.Name.LocalName == "ConnectString"))
            {
                var val = elem.Value;
                if (ShouldAppendTrustServerCertificate(val, null, allowUntrusted))
                {
                    elem.Value = val.TrimEnd(';') + ";TrustServerCertificate=True;";
                    modified = true;
                }
            }

            if (modified)
            {
                using var outMs = new MemoryStream();
                doc.Save(outMs);
                return outMs.ToArray();
            }
        }
        catch { }
        return rdlBytes;
    }

    private IEnumerable<string> ChildFolders(string folder)
    {
        // Folders come from discovered Folder items plus the parents of every staged item,
        // so the tree is complete even when SSRS did not list a folder explicitly.
        var all = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in _catalog.Items)
        {
            var path = item.ItemType == MigrationItemType.Folder
                ? StagedCatalog.NormalizePath(item.SourcePath)
                : StagedCatalog.ParentOf(item.SourcePath);
            while (path != "/")
            {
                all.Add(path);
                path = StagedCatalog.ParentOf(path);
            }
        }

        return all
            .Where(p => string.Equals(StagedCatalog.ParentOf(p), folder, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase);
    }

    private static CatalogItem ToBoldItem(MigrationCatalogItem item, ItemTypeEnum type) => new()
    {
        // The SSRS path is the identifier the designer hands back when an item is chosen.
        Id = StagedCatalog.NormalizePath(item.SourcePath),
        Name = item.Name,
        Type = type
    };
}
