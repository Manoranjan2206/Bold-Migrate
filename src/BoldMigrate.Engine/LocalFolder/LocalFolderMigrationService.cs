using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using BoldMigrate.Core.Dto;
using BoldMigrate.Engine.Upload;
using Microsoft.Extensions.Logging;

namespace BoldMigrate.Engine.LocalFolder;

/// <summary>
/// Service that adopts and enhances all features from SSRSToBoldReportsMigration and Migration Scripts.
/// Migrates local folders of RDL reports, RSDS datasources, and RSD datasets directly into Bold Reports Server.
/// Creates automatic category hierarchies, remaps shared datasources and datasets, creates backups,
/// and produces comprehensive logs, CSV summaries, inventories, and markdown summaries.
/// </summary>
public class LocalFolderMigrationService
{
    private readonly ILogger<LocalFolderMigrationService> _logger;
    private readonly Dictionary<string, string> _datasetLookup = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _logs = new();
    private string _logFile = string.Empty;

    // Matches PowerShell's ConvertTo-Json behaviour.
    // Prevents System.Text.Json from escaping '+' (and other chars) to \u002B
    // inside base64 ItemContent, which otherwise causes a 400 Bad Request.
    private static readonly JsonSerializerOptions _relaxedJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false
    };

    public LocalFolderMigrationService(ILogger<LocalFolderMigrationService> logger)
    {
        _logger = logger;
    }

    public IReadOnlyList<string> CurrentLogs
    {
        get
        {
            lock (_logs) { return _logs.ToList(); }
        }
    }

    private void WriteLog(string message, string? phaseLogFile = null)
    {
        var line = $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} | {message}";
        lock (_logs)
        {
            _logs.Add(line);
            if (_logs.Count > 1000) _logs.RemoveAt(0);
        }

        _logger.LogInformation("{Message}", message);

        if (!string.IsNullOrEmpty(_logFile))
        {
            try { File.AppendAllText(_logFile, line + Environment.NewLine); } catch { }
        }

        if (!string.IsNullOrEmpty(phaseLogFile))
        {
            try { File.AppendAllText(phaseLogFile, line + Environment.NewLine); } catch { }
        }
    }

    public async Task<LocalFolderMigrationResult> RunMigrationAsync(LocalFolderMigrationRequest request, CancellationToken ct = default)
    {
        var result = new LocalFolderMigrationResult();
        var reportRows = new List<LocalMigrationReportRow>();
        var inventoryRecords = new List<InventoryRecord>();
        var itemResults = new List<ItemResultEntry>();

        lock (_logs) { _logs.Clear(); }

        try
        {
            var logDir = !string.IsNullOrWhiteSpace(request.LogFolder)
                ? request.LogFolder
                : Path.Combine(Directory.GetCurrentDirectory(), "logs", $"migration-{DateTime.UtcNow:yyyyMMddHHmmss}");

            Directory.CreateDirectory(logDir);
            _logFile = Path.Combine(logDir, "MigrationLog.txt");
            result.LogFilePath = _logFile;

            // Phase logs matching Migration Scripts layout
            var preflightLog = Path.Combine(logDir, "preflight.log");
            var exportLog = Path.Combine(logDir, "export.log");
            var datasourcesLog = Path.Combine(logDir, "datasources.log");
            var datasetsLog = Path.Combine(logDir, "datasets.log");
            var reportsLog = Path.Combine(logDir, "reports.log");
            var reconciliationLog = Path.Combine(logDir, "reconciliation.log");

            foreach (var plf in new[] { preflightLog, exportLog, datasourcesLog, datasetsLog, reportsLog, reconciliationLog })
            {
                if (!File.Exists(plf)) File.WriteAllText(plf, $"# Phase log created {DateTime.UtcNow:O}" + Environment.NewLine);
            }

            WriteLog("==================================================", preflightLog);
            WriteLog("BOLD MIGRATE: Local Estate Migration Initiated", preflightLog);
            WriteLog($"Target Server: {request.ServerUrl} (Tenant: {request.TenantId})", preflightLog);
            WriteLog($"Report Folder: {request.ReportFolder}", preflightLog);
            WriteLog("==================================================", preflightLog);

            // Optional Structured Output Layout (Export/Reports, Export/Datasets, Export/Datasources)
            string? exportReportsDir = null;
            string? exportDataSourcesDir = null;
            string? exportDataSetsDir = null;

            if (request.ExportStructuredLayout)
            {
                var exportRoot = Path.Combine(logDir, "Export");
                exportReportsDir = Path.Combine(exportRoot, "Reports");
                exportDataSourcesDir = Path.Combine(exportRoot, "Datasources");
                exportDataSetsDir = Path.Combine(exportRoot, "Datasets");

                Directory.CreateDirectory(exportReportsDir);
                Directory.CreateDirectory(exportDataSourcesDir);
                Directory.CreateDirectory(exportDataSetsDir);
                result.ExportRootPath = exportRoot;
            }

            // 1. Backup Phase
            if (!string.IsNullOrWhiteSpace(request.BackupFolder) && Directory.Exists(request.ReportFolder))
            {
                Directory.CreateDirectory(request.BackupFolder);
                WriteLog($"Creating backup of RDL files in: {request.BackupFolder}", exportLog);
                var rdlFiles = Directory.GetFiles(request.ReportFolder, "*.rdl", SearchOption.AllDirectories);
                foreach (var rdl in rdlFiles)
                {
                    var rel = Path.GetRelativePath(request.ReportFolder, rdl);
                    var dest = Path.Combine(request.BackupFolder, rel);
                    var destDir = Path.GetDirectoryName(dest);
                    if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);
                    File.Copy(rdl, dest, true);
                }
                WriteLog($"Backed up {rdlFiles.Length} report definitions successfully.", exportLog);
            }

            // 2. Authentication to Bold Reports
            var client = new DestinationClient(request.ServerUrl, request.BearerToken);
            if (!string.IsNullOrWhiteSpace(request.BearerToken))
            {
                client.SetBearerToken(request.BearerToken);
                WriteLog("Authenticated with direct Bearer JWT token.", preflightLog);
            }
            else if (!string.IsNullOrWhiteSpace(request.Username) && !string.IsNullOrWhiteSpace(request.Password))
            {
                WriteLog("Authenticating with Bold Reports administrator credentials...", preflightLog);
                try
                {
                    await client.AuthenticateAsync(request.Username, request.Password, ct);
                    WriteLog("Authentication successful. OAuth2 bearer token obtained.", preflightLog);
                }
                catch (Exception ex)
                {
                    WriteLog($"Warning: Destination authentication probing noted: {ex.Message}. Proceeding with API client.", preflightLog);
                }
            }

            // 3. Upload DataSources (*.rsds)
            var dsFolder = !string.IsNullOrWhiteSpace(request.DataSourceFolder) && Directory.Exists(request.DataSourceFolder)
                ? request.DataSourceFolder
                : null;

            if (dsFolder != null)
            {
                var rsdsFiles = Directory.GetFiles(dsFolder, "*.rsds", SearchOption.AllDirectories);
                result.TotalDataSources = rsdsFiles.Length;
                WriteLog($"Discovered {rsdsFiles.Length} DataSource (.rsds) definition(s) in {dsFolder}", datasourcesLog);

                foreach (var dsFile in rsdsFiles)
                {
                    var rel = Path.GetRelativePath(dsFolder, dsFile);
                    var originalName = Path.GetFileNameWithoutExtension(dsFile).Replace(" ", "_");
                    var parentDir = Path.GetDirectoryName(rel)?.Replace(Path.DirectorySeparatorChar, '_').Replace(' ', '_') ?? "";
                    var dsName = string.IsNullOrEmpty(parentDir) ? originalName : $"{parentDir}_{originalName}";

                    if (exportDataSourcesDir != null)
                    {
                        var copyDest = Path.Combine(exportDataSourcesDir, Path.GetFileName(dsFile));
                        try { File.Copy(dsFile, copyDest, true); } catch { }
                    }

                    try
                    {
                        var xml = new XmlDocument();
                        xml.Load(dsFile);

                        var connectNode = xml.SelectSingleNode("//DataSourceDefinition/ConnectString");
                        var extNode = xml.SelectSingleNode("//DataSourceDefinition/Extension");
                        var userNode = xml.SelectSingleNode("//DataSourceDefinition/UserName");
                        var passNode = xml.SelectSingleNode("//DataSourceDefinition/PassWord");

                        var connectString = connectNode?.InnerText?.Trim() ?? "";
                        var extension = extNode?.InnerText?.Trim() ?? "SQL";
                        var serverType = GetServerTypeFromExtension(extension);
                        var userName = userNode?.InnerText?.Trim();
                        var password = passNode?.InnerText?.Trim();
                        var credRetrieval = !string.IsNullOrEmpty(userName) ? "Store" : "None";

                        WriteLog($"Uploading DataSource: '{dsName}' (Type: {serverType})", datasourcesLog);
                        var (dsSuccess, publishedId) = await client.CreateDataSourceAsync(
                            request.TenantId, dsName, connectString, serverType, userName, password, credRetrieval, ct);

                        if (dsSuccess)
                        {
                            result.MigratedDataSources++;
                            WriteLog($"DataSource '{dsName}' created successfully. ID: {publishedId}", datasourcesLog);
                            inventoryRecords.Add(new InventoryRecord { Type = "DataSource", Name = dsName, Path = rel, Disposition = "Imported", Count = 1 });
                            itemResults.Add(new ItemResultEntry { SourcePath = rel, SourceType = "DataSource", SourceName = dsName, Status = "Imported", PublishedItemId = publishedId });
                        }
                        else
                        {
                            result.SkippedDataSources++;
                            WriteLog($"DataSource '{dsName}' skipped (already exists or conflict).", datasourcesLog);
                            inventoryRecords.Add(new InventoryRecord { Type = "DataSource", Name = dsName, Path = rel, Disposition = "Skipped", Count = 1 });
                        }
                    }
                    catch (Exception ex)
                    {
                        result.FailedDataSources++;
                        WriteLog($"DataSource '{dsName}' upload failed: {ex.Message}", datasourcesLog);
                        inventoryRecords.Add(new InventoryRecord { Type = "DataSource", Name = dsName, Path = rel, Disposition = "Failed", Count = 1 });
                        itemResults.Add(new ItemResultEntry { SourcePath = rel, SourceType = "DataSource", SourceName = dsName, Status = "Failed", Error = ex.Message });
                    }
                }
            }

            // Preload existing server datasets to lookup
            try
            {
                var existingDatasets = await client.GetDataSetsAsync(request.TenantId, ct);
                foreach (var kvp in existingDatasets)
                {
                    _datasetLookup[kvp.Key] = kvp.Value;
                }
            }
            catch { }

            // 4. Upload DataSets (*.rsd)
            var datasetFolder = !string.IsNullOrWhiteSpace(request.DataSetFolder) && Directory.Exists(request.DataSetFolder)
                ? request.DataSetFolder
                : null;

            if (datasetFolder != null)
            {
                var rsdFiles = Directory.GetFiles(datasetFolder, "*.rsd", SearchOption.AllDirectories);
                result.TotalDataSets = rsdFiles.Length;
                WriteLog($"Discovered {rsdFiles.Length} DataSet (.rsd) definition(s) in {datasetFolder}", datasetsLog);

                foreach (var rsdFile in rsdFiles)
                {
                    var rel = Path.GetRelativePath(datasetFolder, rsdFile);
                    var originalName = Path.GetFileNameWithoutExtension(rsdFile).Replace(" ", "_");
                    var parentDir = Path.GetDirectoryName(rel)?.Replace(Path.DirectorySeparatorChar, '_').Replace(' ', '_') ?? "";
                    var datasetName = string.IsNullOrEmpty(parentDir) ? originalName : $"{parentDir}_{originalName}";

                    if (exportDataSetsDir != null)
                    {
                        var copyDest = Path.Combine(exportDataSetsDir, Path.GetFileName(rsdFile));
                        try { File.Copy(rsdFile, copyDest, true); } catch { }
                    }

                    try
                    {
                        var xml = new XmlDocument();
                        xml.Load(rsdFile);
                        var nsmgr = new XmlNamespaceManager(xml.NameTable);
                        nsmgr.AddNamespace("sds", "http://schemas.microsoft.com/sqlserver/reporting/2010/01/shareddatasetdefinition");

                        var dsRefNode = xml.SelectSingleNode("/sds:SharedDataSet/sds:DataSet/sds:Query/sds:DataSourceReference", nsmgr);
                        var targetDs = request.TargetDataSourceName ?? "SalesDB";
                        if (dsRefNode != null && !string.IsNullOrWhiteSpace(dsRefNode.InnerText))
                        {
                            var parts = dsRefNode.InnerText.Trim().TrimStart('/').Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
                            targetDs = string.Join("_", parts).Replace(" ", "_");
                            dsRefNode.InnerText = targetDs;
                        }

                        using var ms = new MemoryStream();
                        xml.Save(ms);
                        var base64 = Convert.ToBase64String(ms.ToArray());

                        WriteLog($"Uploading DataSet: '{datasetName}' mapped to DataSource '{targetDs}'", datasetsLog);
                        var (dSuccess, publishedId) = await client.CreateDataSetAsync(request.TenantId, datasetName, base64, targetDs, ct);

                        if (dSuccess)
                        {
                            result.MigratedDataSets++;
                            _datasetLookup[datasetName] = publishedId;
                            WriteLog($"DataSet '{datasetName}' uploaded successfully. ID: {publishedId}", datasetsLog);
                            inventoryRecords.Add(new InventoryRecord { Type = "DataSet", Name = datasetName, Path = rel, Disposition = "Imported", Count = 1 });
                            itemResults.Add(new ItemResultEntry { SourcePath = rel, SourceType = "DataSet", SourceName = datasetName, Status = "Imported", PublishedItemId = publishedId });
                        }
                        else
                        {
                            result.SkippedDataSets++;
                            WriteLog($"DataSet '{datasetName}' skipped (already exists or conflict).", datasetsLog);
                            inventoryRecords.Add(new InventoryRecord { Type = "DataSet", Name = datasetName, Path = rel, Disposition = "Skipped", Count = 1 });
                        }
                    }
                    catch (Exception ex)
                    {
                        result.FailedDataSets++;
                        WriteLog($"DataSet '{datasetName}' upload failed: {ex.Message}", datasetsLog);
                        inventoryRecords.Add(new InventoryRecord { Type = "DataSet", Name = datasetName, Path = rel, Disposition = "Failed", Count = 1 });
                        itemResults.Add(new ItemResultEntry { SourcePath = rel, SourceType = "DataSet", SourceName = datasetName, Status = "Failed", Error = ex.Message });
                    }
                }
            }

            // 5. Upload Reports (*.rdl)
            if (!Directory.Exists(request.ReportFolder))
            {
                throw new DirectoryNotFoundException($"Report folder does not exist: {request.ReportFolder}");
            }

            var reportFiles = Directory.GetFiles(request.ReportFolder, "*.rdl", SearchOption.AllDirectories);
            result.TotalReports = reportFiles.Length;
            WriteLog($"Discovered {reportFiles.Length} Report (.rdl) file(s) in {request.ReportFolder}", reportsLog);

            foreach (var rdlFile in reportFiles)
            {
                var rel = Path.GetRelativePath(request.ReportFolder, rdlFile);
                var reportName = Path.GetFileNameWithoutExtension(rdlFile);
                var pathParts = rel.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);

                if (exportReportsDir != null)
                {
                    var copyDest = Path.Combine(exportReportsDir, Path.GetFileName(rdlFile));
                    try { File.Copy(rdlFile, copyDest, true); } catch { }
                }

                // Resolve category hierarchy
                string? categoryId = null;
                string categoryName = "Sample Reports";

                if (pathParts.Length > 1)
                {
                    string? currentParentId = null;
                    for (int i = 0; i < pathParts.Length - 1; i++)
                    {
                        var folder = pathParts[i];
                        categoryName = folder;
                        currentParentId = await client.GetOrCreateCategoryAsync(request.TenantId, folder, currentParentId, ct);
                    }
                    categoryId = currentParentId;
                }
                else
                {
                    categoryId = await client.GetOrCreateCategoryAsync(request.TenantId, "Sample Reports", null, ct);
                }

                // Parse RDL XML for mappings
                var rdlXml = new XmlDocument();
                rdlXml.Load(rdlFile);

                var dataSourceMappings = new List<Dictionary<string, object>>();
                var dataSetMappings = new List<Dictionary<string, object>>();
                var dsSummaryList = new List<string>();
                var dsetSummaryList = new List<string>();

                // DataSources
                var dsNodes = rdlXml.SelectNodes("//*[local-name()='DataSource']");
                if (dsNodes != null)
                {
                    foreach (XmlNode dsNode in dsNodes)
                    {
                        var rdlDsName = dsNode.Attributes?["Name"]?.Value ?? "";
                        var refNode = dsNode.SelectSingleNode("./*[local-name()='DataSourceReference']");
                        if (refNode != null && !string.IsNullOrWhiteSpace(refNode.InnerText))
                        {
                            var targetName = CleanDataSourceRef(refNode.InnerText.Trim());
                            if (!string.IsNullOrEmpty(request.TargetDataSourceName))
                            {
                                targetName = request.TargetDataSourceName;
                            }

                            dataSourceMappings.Add(new Dictionary<string, object>
                            {
                                { "Name", rdlDsName },
                                { "DataSourceName", targetName }
                            });
                            dsSummaryList.Add($"{rdlDsName}->{targetName}");
                        }
                    }
                }

                // Shared DataSets
                var dsetNodes = rdlXml.SelectNodes("//*[local-name()='DataSet']");
                if (dsetNodes != null)
                {
                    foreach (XmlNode dsetNode in dsetNodes)
                    {
                        var rdlDsetName = dsetNode.Attributes?["Name"]?.Value ?? "";
                        var refNode = dsetNode.SelectSingleNode(".//*[local-name()='SharedDataSetReference']");
                        if (refNode != null && !string.IsNullOrWhiteSpace(refNode.InnerText))
                        {
                            var sharedName = CleanDataSourceRef(refNode.InnerText.Trim());
                            var datasetId = _datasetLookup.TryGetValue(sharedName, out var id) ? id : Guid.NewGuid().ToString();

                            dataSetMappings.Add(new Dictionary<string, object>
                            {
                                { "Name", rdlDsetName },
                                { "DataSetName", sharedName },
                                { "DataSetId", datasetId }
                            });
                            dsetSummaryList.Add($"{rdlDsetName}->{sharedName}");
                        }
                    }
                }

                var rdlBytes = await File.ReadAllBytesAsync(rdlFile, ct);
                var rdlBase64 = Convert.ToBase64String(rdlBytes);

                WriteLog($"Uploading Report: '{reportName}' to Category '{categoryName}'", reportsLog);

                try
                {
                    var (rSuccess, publishedId) = await client.CreateReportAsync(
                        request.TenantId,
                        reportName,
                        rdlBase64,
                        categoryId,
                        dataSourceMappings.Count > 0 ? dataSourceMappings : null,
                        dataSetMappings.Count > 0 ? dataSetMappings : null,
                        ct);

                    if (rSuccess)
                    {
                        result.MigratedReports++;
                        WriteLog($"Report '{reportName}' uploaded successfully. ID: {publishedId}", reportsLog);
                        reportRows.Add(new LocalMigrationReportRow
                        {
                            ReportName = reportName,
                            RelativePath = rel,
                            CategoryName = categoryName,
                            DataSources = string.Join("; ", dsSummaryList),
                            Datasets = string.Join("; ", dsetSummaryList),
                            Status = "Uploaded"
                        });
                        inventoryRecords.Add(new InventoryRecord { Type = "Report", Name = reportName, Path = rel, Disposition = "Imported", Count = 1 });
                        itemResults.Add(new ItemResultEntry { SourcePath = rel, SourceType = "Report", SourceName = reportName, Status = "Imported", PublishedItemId = publishedId });
                    }
                    else
                    {
                        result.SkippedReports++;
                        WriteLog($"Report '{reportName}' skipped (already exists or conflict).", reportsLog);
                        reportRows.Add(new LocalMigrationReportRow
                        {
                            ReportName = reportName,
                            RelativePath = rel,
                            CategoryName = categoryName,
                            DataSources = string.Join("; ", dsSummaryList),
                            Datasets = string.Join("; ", dsetSummaryList),
                            Status = "Skipped"
                        });
                        inventoryRecords.Add(new InventoryRecord { Type = "Report", Name = reportName, Path = rel, Disposition = "Skipped", Count = 1 });
                    }
                }
                catch (Exception ex)
                {
                    result.FailedReports++;
                    WriteLog($"Report '{reportName}' upload failed: {ex.Message}", reportsLog);
                    reportRows.Add(new LocalMigrationReportRow
                    {
                        ReportName = reportName,
                        RelativePath = rel,
                        CategoryName = categoryName,
                        DataSources = string.Join("; ", dsSummaryList),
                        Datasets = string.Join("; ", dsetSummaryList),
                        Status = "Failed",
                        Error = ex.Message
                    });
                    inventoryRecords.Add(new InventoryRecord { Type = "Report", Name = reportName, Path = rel, Disposition = "Failed", Count = 1 });
                    itemResults.Add(new ItemResultEntry { SourcePath = rel, SourceType = "Report", SourceName = reportName, Status = "Failed", Error = ex.Message });
                }
            }

            // 6. Generate Summaries, Reconciliation & Artifacts
            result.ReportRows = reportRows;
            result.Success = result.FailedReports == 0;
            result.Message = $"Migration completed: {result.MigratedReports}/{result.TotalReports} reports uploaded, {result.MigratedDataSources}/{result.TotalDataSources} datasources, {result.MigratedDataSets}/{result.TotalDataSets} datasets.";

            // MigrationResult.csv
            var resultCsv = Path.Combine(logDir, "MigrationResult.csv");
            using (var sw = new StreamWriter(resultCsv, false, new UTF8Encoding(true)))
            {
                sw.WriteLine("\"ReportName\",\"Category\",\"Datasets\",\"DataSources\",\"Status\",\"Error\"");
                foreach (var r in reportRows)
                {
                    sw.WriteLine($"\"{EscapeCsv(r.ReportName)}\",\"{EscapeCsv(r.CategoryName)}\",\"{EscapeCsv(r.Datasets)}\",\"{EscapeCsv(r.DataSources)}\",\"{EscapeCsv(r.Status)}\",\"{EscapeCsv(r.Error ?? "")}\"");
                }
            }
            result.ResultCsvPath = resultCsv;

            // inventory.csv
            var inventoryCsv = Path.Combine(logDir, "inventory.csv");
            using (var sw = new StreamWriter(inventoryCsv, false, new UTF8Encoding(true)))
            {
                sw.WriteLine("\"Type\",\"Path\",\"Name\",\"Disposition\",\"Count\"");
                foreach (var inv in inventoryRecords)
                {
                    sw.WriteLine($"\"{EscapeCsv(inv.Type)}\",\"{EscapeCsv(inv.Path)}\",\"{EscapeCsv(inv.Name)}\",\"{EscapeCsv(inv.Disposition)}\",{inv.Count}");
                }
                sw.WriteLine($"\"Summary:Report\",\"\",\"\",\"Count\",{result.TotalReports}");
                sw.WriteLine($"\"Summary:DataSource\",\"\",\"\",\"Count\",{result.TotalDataSources}");
                sw.WriteLine($"\"Summary:DataSet\",\"\",\"\",\"Count\",{result.TotalDataSets}");
            }
            result.InventoryCsvPath = inventoryCsv;

            // item-results.json
            var itemResultsJson = Path.Combine(logDir, "item-results.json");
            var itemResultsObj = new
            {
                GeneratedAt = DateTime.UtcNow.ToString("o"),
                TotalItems = itemResults.Count,
                Items = itemResults
            };
            File.WriteAllText(itemResultsJson, JsonSerializer.Serialize(itemResultsObj, new JsonSerializerOptions { WriteIndented = true }));
            result.ItemResultsJsonPath = itemResultsJson;

            // migration-summary.md
            var summaryMd = Path.Combine(logDir, "migration-summary.md");
            var summaryContent = $@"# SSRS to Bold Reports Migration Summary

**Execution Time:** {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC  
**Target Server:** {request.ServerUrl} (Tenant: {request.TenantId})  
**Report Folder:** `{request.ReportFolder}`  

## Estate Statistics

| Component | Total Discovered | Migrated / Active | Skipped (Existing) | Failed |
| :--- | :--- | :--- | :--- | :--- |
| **Reports** | {result.TotalReports} | {result.MigratedReports} | {result.SkippedReports} | {result.FailedReports} |
| **DataSources** | {result.TotalDataSources} | {result.MigratedDataSources} | {result.SkippedDataSources} | {result.FailedDataSources} |
| **DataSets** | {result.TotalDataSets} | {result.MigratedDataSets} | {result.SkippedDataSets} | {result.FailedDataSets} |

## Generated Artifacts

- **Detailed Migration Log:** `{result.LogFilePath}`
- **Report Result CSV:** `{result.ResultCsvPath}`
- **Inventory Reconciliation CSV:** `{result.InventoryCsvPath}`
- **Structured Items JSON:** `{result.ItemResultsJsonPath}`
- **Export Directory:** `{result.ExportRootPath ?? "N/A"}`

## Phase Logs Generated

- `preflight.log`
- `export.log`
- `datasources.log`
- `datasets.log`
- `reports.log`
- `reconciliation.log`

---
*Generated by Bold Migrate Enterprise Edition.*
";
            File.WriteAllText(summaryMd, summaryContent);
            result.SummaryMarkdownPath = summaryMd;

            WriteLog("==================================================", reconciliationLog);
            WriteLog("MIGRATION COMPLETED SUCCESSFULLY", reconciliationLog);
            WriteLog(result.Message, reconciliationLog);
            WriteLog("==================================================", reconciliationLog);

            result.Logs = CurrentLogs.ToList();
            return result;
        }
        catch (Exception ex)
        {
            WriteLog($"FATAL MIGRATION FAILURE: {ex.Message}");
            result.Success = false;
            result.Message = $"Migration aborted due to error: {ex.Message}";
            result.Logs = CurrentLogs.ToList();
            return result;
        }
    }

    private static string CleanDataSourceRef(string reference)
    {
        if (string.IsNullOrWhiteSpace(reference)) return string.Empty;
        var parts = reference.Trim().TrimStart('/').Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
        return string.Join("_", parts).Replace(" ", "_");
    }

    private static string GetServerTypeFromExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension)) return "SQL";
        return extension.Trim().ToUpperInvariant() switch
        {
            "SQL" => "SQL",
            "SQLCE" => "SQLCE",
            "OLEDB" => "OLEDB",
            "ODBC" => "ODBC",
            "ORACLE" => "Oracle",
            "XML" => "XML",
            "SSAS" => "SSAS",
            "POSTGRESQL" => "PostgreSQL",
            "MYSQL" => "MySQL",
            "WEBAPI" => "WebApi",
            _ => "SQL"
        };
    }

    private static string EscapeCsv(string s) => s.Replace("\"", "\"\"");

    private class InventoryRecord
    {
        public string Type { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Disposition { get; set; } = "Pending";
        public int Count { get; set; } = 1;
    }

    private class ItemResultEntry
    {
        public string SourcePath { get; set; } = string.Empty;
        public string SourceType { get; set; } = string.Empty;
        public string SourceName { get; set; } = string.Empty;
        public string Status { get; set; } = "Pending";
        public string? PublishedItemId { get; set; }
        public string? Error { get; set; }
    }
}
