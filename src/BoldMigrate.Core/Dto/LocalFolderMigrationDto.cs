namespace BoldMigrate.Core.Dto;

/// <summary>
/// Options for executing a local folder migration from RDL/RSDS/RSD files into Bold Reports Server.
/// Adopts full capabilities from SSRSToBoldReportsMigration and Migration Scripts.
/// </summary>
public class LocalFolderMigrationRequest
{
    /// <summary>Bold Reports Server base URL (e.g. http://localhost:8090 or https://reports.example.com)</summary>
    public string ServerUrl { get; set; } = string.Empty;

    /// <summary>Tenant / Site identifier for Bold Reports (e.g. site1, site4)</summary>
    public string TenantId { get; set; } = "site1";

    /// <summary>Username / Admin email for Bold Reports authentication</summary>
    public string? Username { get; set; }

    /// <summary>Password for Bold Reports authentication</summary>
    public string? Password { get; set; }

    /// <summary>Optional direct Bearer JWT Token (from Configuration.json or OAuth)</summary>
    public string? BearerToken { get; set; }

    /// <summary>Local directory containing .rdl files to migrate (scanned recursively)</summary>
    public string ReportFolder { get; set; } = string.Empty;

    /// <summary>Optional directory containing .rsds shared datasource definitions</summary>
    public string? DataSourceFolder { get; set; }

    /// <summary>Optional directory containing .rsd shared dataset definitions</summary>
    public string? DataSetFolder { get; set; }

    /// <summary>Optional backup directory where original RDL files are backed up before migration</summary>
    public string? BackupFolder { get; set; }

    /// <summary>Directory where migration logs, CSV summaries, and inventories are written</summary>
    public string? LogFolder { get; set; }

    /// <summary>Target shared datasource name to map shared-datasource reports to (optional fallback)</summary>
    public string? TargetDataSourceName { get; set; }

    /// <summary>Whether to generate structured output folder layout (Export/Reports, Export/Datasets, logs/)</summary>
    public bool ExportStructuredLayout { get; set; } = true;
}

/// <summary>
/// Execution result and statistics for local folder migration.
/// </summary>
public class LocalFolderMigrationResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;

    // Report stats
    public int TotalReports { get; set; }
    public int MigratedReports { get; set; }
    public int FailedReports { get; set; }
    public int SkippedReports { get; set; }

    // DataSource stats
    public int TotalDataSources { get; set; }
    public int MigratedDataSources { get; set; }
    public int FailedDataSources { get; set; }
    public int SkippedDataSources { get; set; }

    // DataSet stats
    public int TotalDataSets { get; set; }
    public int MigratedDataSets { get; set; }
    public int FailedDataSets { get; set; }
    public int SkippedDataSets { get; set; }

    // Generated Artifacts
    public string? LogFilePath { get; set; }
    public string? ResultCsvPath { get; set; }
    public string? InventoryCsvPath { get; set; }
    public string? ItemResultsJsonPath { get; set; }
    public string? SummaryMarkdownPath { get; set; }
    public string? ExportRootPath { get; set; }

    // In-memory real-time log lines
    public List<string> Logs { get; set; } = new();

    public List<LocalMigrationReportRow> ReportRows { get; set; } = new();
}

public class LocalMigrationReportRow
{
    public string ReportName { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public string CategoryName { get; set; } = string.Empty;
    public string Datasets { get; set; } = string.Empty;
    public string DataSources { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? Error { get; set; }
}
