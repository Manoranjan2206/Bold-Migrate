using BoldMigrate.Core.Dto;
using BoldMigrate.Engine.LocalFolder;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BoldMigrate.Engine.Tests.LocalFolder;

public class LocalFolderMigrationServiceTests : IDisposable
{
    private readonly string _testRoot;
    private readonly string _reportDir;
    private readonly string _backupDir;
    private readonly string _logDir;
    private readonly string _dsDir;
    private readonly string _dsetDir;

    public LocalFolderMigrationServiceTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "BoldMigrateTest_" + Guid.NewGuid().ToString("N"));
        _reportDir = Path.Combine(_testRoot, "Reports");
        _backupDir = Path.Combine(_testRoot, "Backup");
        _logDir = Path.Combine(_testRoot, "Logs");
        _dsDir = Path.Combine(_testRoot, "DataSources");
        _dsetDir = Path.Combine(_testRoot, "DataSets");

        Directory.CreateDirectory(_reportDir);
        Directory.CreateDirectory(Path.Combine(_reportDir, "Finance"));
        Directory.CreateDirectory(_dsDir);
        Directory.CreateDirectory(_dsetDir);

        // Create sample RDL file
        var rdlContent = @"<?xml version=""1.0"" encoding=""utf-8""?>
<Report xmlns=""http://schemas.microsoft.com/sqlserver/reporting/2016/01/reportdefinition"">
  <DataSources>
    <DataSource Name=""SalesDB"">
      <DataSourceReference>/DataSources/SalesDB</DataSourceReference>
    </DataSource>
  </DataSources>
  <DataSets>
    <DataSet Name=""SalesData"">
      <SharedDataSet>
        <SharedDataSetReference>/DataSets/SalesSummary</SharedDataSetReference>
      </SharedDataSet>
    </DataSet>
  </DataSets>
</Report>";
        File.WriteAllText(Path.Combine(_reportDir, "Finance", "QuarterlyReport.rdl"), rdlContent);

        // Create sample RSDS file
        var rsdsContent = @"<?xml version=""1.0"" encoding=""utf-8""?>
<RptDataSource xmlns=""http://schemas.microsoft.com/sqlserver/reporting/2006/03/reportdatasource"">
  <ConnectionProperties>
    <Extension>SQL</Extension>
    <ConnectString>Data Source=sql01;Initial Catalog=Sales</ConnectString>
  </ConnectionProperties>
</RptDataSource>";
        File.WriteAllText(Path.Combine(_dsDir, "SalesDB.rsds"), rsdsContent);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testRoot))
            {
                Directory.Delete(_testRoot, true);
            }
        }
        catch { }
    }

    [Fact]
    public async Task RunMigration_CreatesBackupAndStructuredLogs()
    {
        var service = new LocalFolderMigrationService(NullLogger<LocalFolderMigrationService>.Instance);
        var req = new LocalFolderMigrationRequest
        {
            ServerUrl = "http://localhost:8090",
            TenantId = "site1",
            ReportFolder = _reportDir,
            DataSourceFolder = _dsDir,
            DataSetFolder = _dsetDir,
            BackupFolder = _backupDir,
            LogFolder = _logDir,
            ExportStructuredLayout = true
        };

        var result = await service.RunMigrationAsync(req);

        Assert.NotNull(result);
        Assert.Equal(1, result.TotalReports);
        Assert.Equal(1, result.TotalDataSources);

        // Verify backup was created
        var backedUpRdl = Path.Combine(_backupDir, "Finance", "QuarterlyReport.rdl");
        Assert.True(File.Exists(backedUpRdl), "Backup RDL should exist");

        // Verify structured output logs exist
        Assert.True(File.Exists(Path.Combine(_logDir, "MigrationLog.txt")), "MigrationLog.txt should exist");
        Assert.True(File.Exists(Path.Combine(_logDir, "MigrationResult.csv")), "MigrationResult.csv should exist");
        Assert.True(File.Exists(Path.Combine(_logDir, "inventory.csv")), "inventory.csv should exist");
        Assert.True(File.Exists(Path.Combine(_logDir, "item-results.json")), "item-results.json should exist");
        Assert.True(File.Exists(Path.Combine(_logDir, "migration-summary.md")), "migration-summary.md should exist");

        // Verify phase logs exist
        Assert.True(File.Exists(Path.Combine(_logDir, "preflight.log")), "preflight.log should exist");
        Assert.True(File.Exists(Path.Combine(_logDir, "reports.log")), "reports.log should exist");
        Assert.True(File.Exists(Path.Combine(_logDir, "datasources.log")), "datasources.log should exist");

        // Verify CSV content
        var csvLines = File.ReadAllLines(Path.Combine(_logDir, "MigrationResult.csv"));
        Assert.True(csvLines.Length >= 2);
        Assert.Contains("QuarterlyReport", csvLines[1]);
    }
}
