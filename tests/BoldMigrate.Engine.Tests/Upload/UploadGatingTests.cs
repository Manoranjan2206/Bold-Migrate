using BoldMigrate.Core.Enums;
using BoldMigrate.Core.Interfaces;
using BoldMigrate.Core.Models;
using BoldMigrate.Engine.Logging;
using BoldMigrate.Engine.Upload;
using BoldMigrate.Infrastructure.Data;
using BoldMigrate.Infrastructure.Staging;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BoldMigrate.Engine.Tests.Upload;

public class UploadGatingTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly MigrationDbContext _dbContext;
    private readonly MigrationStoreRepository _store;
    private readonly string _stagingDir;
    private readonly FileSystemStagingStore _staging;
    private readonly MigrationLogger _logger;

    public UploadGatingTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<MigrationDbContext>()
            .UseSqlite(_connection)
            .Options;

        _dbContext = new MigrationDbContext(options);
        _dbContext.Database.EnsureCreated();

        _store = new MigrationStoreRepository(_dbContext);
        _stagingDir = Path.Combine(Path.GetTempPath(), "BoldMigrate_UploadGating_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_stagingDir);
        _staging = new FileSystemStagingStore(_stagingDir);
        _logger = new MigrationLogger(NullLogger<MigrationLogger>.Instance, _store);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _connection.Dispose();
        try
        {
            if (Directory.Exists(_stagingDir))
            {
                Directory.Delete(_stagingDir, true);
            }
        }
        catch { }
    }

    [Theory]
    [InlineData(ReportState.AutoPassed, true)]
    [InlineData(ReportState.Approved, true)]
    [InlineData(ReportState.Queued, true)]
    [InlineData(ReportState.Uploaded, true)]
    [InlineData(ReportState.Discovered, false)]
    [InlineData(ReportState.Staged, false)]
    [InlineData(ReportState.Analysed, false)]
    [InlineData(ReportState.Validating, false)]
    [InlineData(ReportState.AwaitingReview, false)]
    [InlineData(ReportState.Flagged, false)]
    [InlineData(ReportState.ValidationFailed, false)]
    [InlineData(ReportState.Blocked, false)]
    [InlineData(ReportState.Excluded, false)]
    [InlineData(ReportState.Uploading, false)]
    [InlineData(ReportState.UploadFailed, false)]
    public void IsEligibleForUpload_ChecksOnlyValidatedAndApprovedStates(ReportState state, bool expected)
    {
        var result = UploadLayer.IsEligibleForUpload(state);
        Assert.Equal(expected, result);
    }

    [Fact]
    public async Task UploadEstateAsync_SkipsUnvalidatedReportsWithGateSkipLog()
    {
        var runId = Guid.NewGuid();
        var run = new MigrationRun
        {
            Id = runId,
            Status = RunStatus.Uploading,
            SourceServerUrl = "http://ssrs.local/reportserver",
            DestinationServerUrl = "http://boldreports.local"
        };
        await _store.CreateRunAsync(run);

        // Add 2 reports that have not passed validation (e.g. Analysed and AwaitingReview)
        var item1 = new CatalogItem
        {
            Id = Guid.NewGuid(),
            RunId = runId,
            ItemType = ItemType.Report,
            Name = "UnvalidatedReport",
            SourcePath = "/Sales/UnvalidatedReport"
        };
        var record1 = new ReportRecord
        {
            CatalogItemId = item1.Id,
            State = ReportState.Analysed
        };

        var item2 = new CatalogItem
        {
            Id = Guid.NewGuid(),
            RunId = runId,
            ItemType = ItemType.Report,
            Name = "ReviewReport",
            SourcePath = "/Finance/ReviewReport"
        };
        var record2 = new ReportRecord
        {
            CatalogItemId = item2.Id,
            State = ReportState.AwaitingReview
        };

        await _store.AddCatalogItemsAsync(new[] { item1, item2 });
        await _store.AddReportRecordAsync(record1);
        await _store.AddReportRecordAsync(record2);

        var uploadLayer = new UploadLayer(_store, _staging, _logger);
        var destClient = new DestinationClient("http://boldreports.local/site/site1", "fake-token");

        var uploadedCount = await uploadLayer.UploadEstateAsync(runId, destClient, () => false, CancellationToken.None);

        // Both reports should be skipped by the validation gate
        Assert.Equal(0, uploadedCount);

        // Verify report states remain unchanged
        var rec1After = await _store.GetReportRecordAsync(item1.Id);
        var rec2After = await _store.GetReportRecordAsync(item2.Id);
        Assert.NotNull(rec1After);
        Assert.NotNull(rec2After);
        Assert.Equal(ReportState.Analysed, rec1After.State);
        Assert.Equal(ReportState.AwaitingReview, rec2After.State);

        // Verify UPLD-GATE-SKIP events were recorded in live logs
        var liveLogs = _logger.GetLiveLog();
        var skipLogs = liveLogs.Where(l => l.EventCode == "UPLD-GATE-SKIP").ToList();

        Assert.Contains(skipLogs, l => l.Message.Contains("UnvalidatedReport"));
        Assert.Contains(skipLogs, l => l.Message.Contains("ReviewReport"));
    }
}
