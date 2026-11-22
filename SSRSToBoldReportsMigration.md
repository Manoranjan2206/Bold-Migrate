# SSRS to Bold Reports Migration Script Guide

This markdown file documents the PowerShell migration script and the output structure it generates.

## Script Purpose

The script in [SSRSToBoldReportsMigration.ps1](SSRSToBoldReportsMigration.ps1) automates the migration of SSRS content into Bold Reports by:

- connecting to the SSRS report server,
- exporting report, dataset, and datasource content,
- creating a structured output layout,
- writing migration logs and summary files,
- and generating a handbook-style folder tree.

## How to Run

You can start the migration using the batch launcher:

```bat
SSRSToBoldReportsMigration.bat
```

Or directly from PowerShell:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\SSRSToBoldReportsMigration.ps1
```

## Prerequisites

Before running the migration script, ensure the following:

1. **ReportingServices PowerShell Module**: Install the SQL Server Reporting Services (SSRS) tools:
   ```powershell
   Install-Module -Name ReportingServicesTools
   ```

2. **SSRS Access**: Valid credentials for connecting to the source SSRS server (username, password, and server URI)

3. **Bold Reports API Access**: 
   - Valid Bold Reports tenant account
   - Bearer token for API authentication
   - Base URL for the Bold Reports server

4. **Folder Permissions**: Write access to the output folder path specified in configuration

5. **Datasource Passwords**: You'll need to provide passwords interactively for any datasources that use stored credentials

## Configuration

The script reads settings from [Configuration.json](Configuration.json). Typical settings include:

- Source SSRS URI
- Output folder path
- Bold Reports base URL
- Tenant name
- Bearer token
- SSRS username and password

## Output Structure

When the script finishes, it creates a folder tree like this:

```text
C:\MigrationRuns\Pilot-001\
├── inventory.csv                     (source files and counts)
├── item-results.json                 (structured results per item)
├── migration-summary.md              (human-readable summary)
├── logs/
│   ├── preflight.log
│   ├── export.log
│   ├── datasources.log
│   ├── datasets.log
│   ├── reports.log
│   └── reconciliation.log
└── Export/                           (staged SSRS files)
    ├── datasources/
    ├── datasets/
    └── reports/
```

## Key Script Logic

The script implements a 4-step migration process:

### Step 1: Export from SSRS
- Connects to the source SSRS server using credentials from `Configuration.json`
- Retrieves all items (Reports, DataSets, DataSources) recursively
- Exports items to local folders (`Datasources/`, `Datasets/`, `Reports/`)
- Tracks inventory and results in CSV and JSON formats

### Step 2: Import DataSources
- Parses exported `.rsds` (RSDS) files from the `Datasources/` folder
- Extracts connection strings and database credentials
- Prompts for passwords if stored credentials are found
- Uploads datasources to Bold Reports API

### Step 3: Import Datasets
- Processes exported `.rsd` (RSD) files from the `Datasets/` folder
- Updates datasource references to match imported datasource names
- Converts RSD content to Base64 for API transmission
- Creates datasets in Bold Reports

### Step 4: Import Reports
- Processes exported `.rdl` (RDL) files from the `Reports/` folder
- Creates category structure in Bold Reports to match SSRS folder hierarchy
- Uploads reports and maps them to imported datasources

## Helper Functions

The script includes the following utility functions:

| Function | Purpose |
|----------|---------|
| `Get-ServerTypeFromExtension` | Converts SSRS extension to Bold Reports server type |
| `Get-RelativePath` | Calculates relative paths for file organization |
| `Get-DataSourceNameFromReference` | Normalizes datasource names from references |
| `New-OutputLayout` | Creates standardized folder structure for migration run |
| `Write-PhaseLog` | Logs timestamped messages to phase-specific log files |
| `Initialize-MigrationOutputs` | Creates inventory CSV and results JSON files |
| `Update-MigrationInventoryRecord` | Updates disposition status in inventory tracking |
| `Update-MigrationItemResult` | Tracks detailed status of each migrated item |
| `Write-MigrationInventoryCsv` | Exports inventory with summaries to CSV |
| `Write-MigrationItemResultsJson` | Exports detailed results to JSON |
| `Get-BoldCategoryId` | Retrieves or creates categories in Bold Reports |

### Example: New-OutputLayout Function

```powershell
function New-OutputLayout {
    param([string]$RootPath)

    $resolvedRoot = [System.IO.Path]::GetFullPath($RootPath)
    $exportRoot = Join-Path $resolvedRoot 'Export'
    $datasourcesRoot = Join-Path $exportRoot 'Datasources'
    $datasetsRoot = Join-Path $exportRoot 'Datasets'
    $reportsRoot = Join-Path $exportRoot 'Reports'
    $logsRoot = Join-Path $resolvedRoot 'logs'

    # Create all required directories
    foreach ($folder in @($resolvedRoot, $exportRoot, $datasourcesRoot, 
                          $datasetsRoot, $reportsRoot, $logsRoot)) {
        New-Item -ItemType Directory -Path $folder -Force | Out-Null
    }

    # Create log files
    foreach ($logFile in @('preflight.log', 'export.log', 'datasources.log', 
                           'datasets.log', 'reports.log', 'reconciliation.log')) {
        New-Item -ItemType File -Path (Join-Path $logsRoot $logFile) -Force | Out-Null
    }

    return [PSCustomObject]@{
        RootPath        = $resolvedRoot
        ExportRoot      = $exportRoot
        DatasourcesRoot = $datasourcesRoot
        DatasetsRoot    = $datasetsRoot
        ReportsRoot     = $reportsRoot
        LogsRoot        = $logsRoot
    }
}
```

## Notes

- **Rerunnable**: The script can be run multiple times; it will skip items that already exist (409 Conflict responses).
- **Auditable**: All migrations are logged with timestamps. Inventory and results are exported to both CSV and JSON formats.
- **Error Handling**: Failed items are logged with detailed error messages for troubleshooting.
- **Credentials**: Passwords are prompted interactively during execution if datasources use stored credentials. Sensitive values should not be hardcoded in configuration files.
- **Reporting Services Tools**: The script requires the `ReportingServices` PowerShell module to be installed (`New-RsWebServiceProxy` cmdlet).
- **API Authentication**: Migration to Bold Reports requires a valid Bearer token for API authentication.

## Migration Output Files

After execution, the output folder contains:

| File | Purpose |
|------|---------|
| `inventory.csv` | Summary of all items by type with disposition status and counts |
| `item-results.json` | Detailed results for each item including status, stages, and IDs |
| `logs/preflight.log` | Preflight validation (folder creation, SSRS connection) |
| `logs/export.log` | SSRS export results and errors |
| `logs/datasources.log` | Datasource import results |
| `logs/datasets.log` | Dataset import results |
| `logs/reports.log` | Report import results |
| `logs/reconciliation.log` | Final reconciliation results |
| `Export/Datasources/` | Exported RSDS (datasource definition) files |
| `Export/Datasets/` | Exported RSD (report dataset definition) files |
| `Export/Reports/` | Exported RDL (report definition) files |

## Related Files

- [SSRSToBoldReportsMigration.ps1](SSRSToBoldReportsMigration.ps1)
- [Configuration.json](Configuration.json)
- [SSRSToBoldReportsMigration.bat](SSRSToBoldReportsMigration.bat)
