using System.Net;
using System.Text;
using BoldMigrate.Core.Enums;
using BoldMigrate.Core.Models;
using BoldMigrate.Engine.Preview;
using BoldMigrate.Engine.Upload;
using Xunit;

namespace BoldMigrate.Engine.Tests;

public sealed class FeatureCapabilitiesTests : IDisposable
{
    private readonly string _tempDir;

    public FeatureCapabilitiesTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "BoldMigrate_FeatTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true);
        }
        catch { }
    }

    private string WriteStaged(string fileName, string xmlContent)
    {
        var path = Path.Combine(_tempDir, fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, xmlContent, Encoding.UTF8);
        return path;
    }

    [Fact]
    public void DependencyVerifier_DetectsBrokenSubreportAndCycle_AndRebindsCleanly()
    {
        var runId = Guid.NewGuid();
        var mainRdl = WriteStaged("Main.rdl", """
            <Report xmlns="http://schemas.microsoft.com/sqlserver/reporting/2016/01/reportdefinition">
              <DataSources><DataSource Name="DS1"><DataSourceReference>/Shared/MissingDS</DataSourceReference></DataSource></DataSources>
              <Body><ReportItems><Subreport Name="Sub1"><ReportName>/Sales/MissingSub</ReportName></Subreport></ReportItems></Body>
            </Report>
            """);

        var goodDsPath = WriteStaged("GoodDS.rsds", "<DataSourceDefinition><Extension>SQL</Extension></DataSourceDefinition>");
        var childRdl = WriteStaged("Child.rdl", """
            <Report xmlns="http://schemas.microsoft.com/sqlserver/reporting/2016/01/reportdefinition">
              <Body><ReportItems><Subreport Name="Loop"><ReportName>/Sales/Child</ReportName></Subreport></ReportItems></Body>
            </Report>
            """);

        var mainId = Guid.NewGuid();
        var items = new List<CatalogItem>
        {
            new() { Id = Guid.NewGuid(), RunId = runId, ItemType = ItemType.DataSource, Name = "ProdDS", SourcePath = "/Shared/ProdDS", StagedFilePath = goodDsPath },
            new() { Id = mainId, RunId = runId, ItemType = ItemType.Report, Name = "Main", SourcePath = "/Sales/Main", StagedFilePath = mainRdl },
            new() { Id = Guid.NewGuid(), RunId = runId, ItemType = ItemType.Report, Name = "Child", SourcePath = "/Sales/Child", StagedFilePath = childRdl }
        };

        var audit = DependencyVerifier.VerifyCatalog(runId, items);
        Assert.Equal(2, audit.TotalReports);
        Assert.Equal(2, audit.ReportsWithIssues);
        Assert.Equal(1, audit.BrokenDataSources);
        Assert.Equal(1, audit.BrokenSubreports);
        Assert.Equal(1, audit.CyclesDetected);

        // Rebind Main.rdl DataSource and break Child's cycle by inspecting Main after rebind
        var bytes = File.ReadAllBytes(mainRdl);
        var (okDs, afterDs) = DependencyVerifier.RebindReferenceInRdl(bytes, "DataSource", "/Shared/MissingDS", "/Shared/ProdDS");
        Assert.True(okDs);
        var (okSub, afterSub) = DependencyVerifier.RebindReferenceInRdl(afterDs, "Subreport", "/Sales/MissingSub", "/Sales/Child");
        Assert.True(okSub);
        File.WriteAllBytes(mainRdl, afterSub);

        var recheck = DependencyVerifier.VerifyCatalog(runId, items);
        var mainAudit = recheck.Reports.Single(r => r.ReportId == mainId);
        Assert.Equal(0, mainAudit.BrokenCount);
        Assert.Equal(0, recheck.BrokenDataSources);
        Assert.Equal(0, recheck.BrokenSubreports);
    }

    [Fact]
    public void UploadLayer_PickUniqueDestinationName_AvoidsCollisions()
    {
        var existing = new[] { "Sales", "Sales_migrated", "Sales_migrated_2" };
        var unique = UploadLayer.PickUniqueDestinationName("Sales", existing);
        Assert.Equal("Sales_migrated_3", unique);
    }

    [Fact]
    public async Task DestinationClient_ConflictPolicies_SkipOverwriteAndRenameWorkCorrectly()
    {
        var seenMethodsAndNames = new List<string>();
        using var handler = new StubHttpHandler(req =>
        {
            var path = req.RequestUri!.AbsolutePath + req.RequestUri.Query;
            if (req.Method == HttpMethod.Get && path.Contains("ItemType=Report", StringComparison.OrdinalIgnoreCase))
            {
                return JsonResponse(HttpStatusCode.OK, """[{"Id":"rep-existing-1","Name":"SalesReport","CategoryId":"cat-1"}]""");
            }
            if (req.Method == HttpMethod.Post && path.EndsWith("/v1.0/reports", StringComparison.OrdinalIgnoreCase))
            {
                var body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                seenMethodsAndNames.Add($"POST:{body}");
                if (body.Contains("\"Name\":\"SalesReport\""))
                {
                    return new HttpResponseMessage(HttpStatusCode.Conflict);
                }
                return JsonResponse(HttpStatusCode.OK, """{"Id":"rep-renamed-2","Status":true}""");
            }
            if (req.Method == HttpMethod.Put && path.EndsWith("/v1.0/reports", StringComparison.OrdinalIgnoreCase))
            {
                var body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                seenMethodsAndNames.Add($"PUT:{body}");
                return JsonResponse(HttpStatusCode.OK, """{"Status":true}""");
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using var client = new DestinationClient("http://boldreports.test/reporting/site/site1", "tok", handler);
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("<Report />"));

        // Skip reuses existing id
        var (okSkip, idSkip, nameSkip) = await client.CreateReportWithPolicyAsync(
            "site1", "SalesReport", b64, "cat-1", null, null, ConflictResolutionPolicy.Skip);
        Assert.True(okSkip);
        Assert.Equal("rep-existing-1", idSkip);
        Assert.Equal("SalesReport", nameSkip);

        // Overwrite issues PUT with ItemId=rep-existing-1
        var (okOw, idOw, _) = await client.CreateReportWithPolicyAsync(
            "site1", "SalesReport", b64, "cat-1", null, null, ConflictResolutionPolicy.Overwrite);
        Assert.True(okOw);
        Assert.Equal("rep-existing-1", idOw);
        Assert.Contains(seenMethodsAndNames, s => s.StartsWith("PUT:") && s.Contains("rep-existing-1"));

        // Rename posts with SalesReport_migrated
        var (okRen, idRen, nameRen) = await client.CreateReportWithPolicyAsync(
            "site1", "SalesReport", b64, "cat-1", null, null, ConflictResolutionPolicy.Rename);
        Assert.True(okRen);
        Assert.Equal("rep-renamed-2", idRen);
        Assert.Equal("SalesReport_migrated", nameRen);
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode code, string json) =>
        new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class StubHttpHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        public StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_responder(request));
    }
}
