using System.Net;
using System.Text;
using BoldMigrate.Core.Configuration;
using BoldMigrate.Core.Dto;
using BoldMigrate.Core.Enums;
using BoldMigrate.Engine.LocalFolder;
using BoldMigrate.Engine.Logging;
using BoldMigrate.Engine.Transform;
using BoldMigrate.Engine.Upload;
using BoldMigrate.Infrastructure.Credentials;
using BoldMigrate.Infrastructure.SourceAdapters;
using BoldMigrate.Infrastructure.Staging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BoldMigrate.Engine.Tests;

public class SecurityAndPipelineTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), "BoldMigrate_SecTests_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
                Directory.Delete(_tempRoot, recursive: true);
        }
        catch { }
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        public List<Uri?> RequestedUris { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestedUris.Add(request.RequestUri);
            return Task.FromResult(handler(request));
        }
    }

    [Fact]
    public async Task FileSystemStagingStore_BlocksPathTraversal_And_StagesOriginalsSeparately()
    {
        var staging = new FileSystemStagingStore(_tempRoot);
        var runId = Guid.NewGuid();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await staging.StageFileAsync(runId, "/Finance/../../evil.txt", [1, 2, 3]));

        var originalBytes = "<Report>orig</Report>"u8.ToArray();
        var stagedBytes = "<Report>xfrm</Report>"u8.ToArray();

        await staging.StageOriginalAsync(runId, "/Finance/Summary", originalBytes);
        var stagedPath = await staging.StageFileAsync(runId, "/Finance/Summary", stagedBytes);
        var originalPath = staging.ResolveOriginalPath(runId, "/Finance/Summary");

        Assert.NotNull(originalPath);
        Assert.NotEqual(stagedPath, originalPath);
        Assert.Equal(originalBytes, File.ReadAllBytes(originalPath!));
        Assert.Equal(stagedBytes, await staging.ReadStagedFileAsync(stagedPath));

        var outsideFile = Path.Combine(Path.GetTempPath(), $"outside-{Guid.NewGuid():N}.txt");
        File.WriteAllText(outsideFile, "secret");
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await staging.ReadStagedFileAsync(outsideFile));
        }
        finally
        {
            File.Delete(outsideFile);
        }
    }

    [Fact]
    public void InMemoryCredentialStore_DpapiLocalMode_PersistsAndReloadsEncryptedVault()
    {
        var vaultDir = Path.Combine(_tempRoot, "vault");
        using (var store1 = new InMemoryCredentialStore(vaultDir))
        {
            store1.SetMode(CredentialMode.DpapiLocal);
            store1.SetSourceCredential("run1", new NetworkCredential("alice", "P@ssw0rd!", "CORP"));
            store1.SetDataSourceCredential("ds1", new NetworkCredential("sa", "SqlSecret!"));
            store1.SetDestinationToken("jwt-token-xyz");
        }

        var vaultFile = Path.Combine(vaultDir, "credentials.vault");
        Assert.True(File.Exists(vaultFile));
        var rawVaultText = Encoding.UTF8.GetString(File.ReadAllBytes(vaultFile));
        Assert.DoesNotContain("P@ssw0rd!", rawVaultText);
        Assert.DoesNotContain("SqlSecret!", rawVaultText);
        Assert.DoesNotContain("jwt-token-xyz", rawVaultText);

        using var store2 = new InMemoryCredentialStore(vaultDir);
        Assert.Equal(CredentialMode.DpapiLocal, store2.ActiveMode);
        Assert.Equal("alice", store2.GetSourceCredential("run1")?.UserName);
        Assert.Equal("P@ssw0rd!", store2.GetSourceCredential("run1")?.Password);
        Assert.Equal("SqlSecret!", store2.GetDataSourceCredential("ds1")?.Password);
        Assert.Equal("jwt-token-xyz", store2.GetDestinationToken());
    }

    [Fact]
    public void Redactor_MasksQuotedSecretsKeysAndXmlPasswords()
    {
        var conn = "Server=db;Database=Sales;User Id=sa;Password=\"p@ss;123\";AccountKey='secretKey==';";
        var redactedConn = Redactor.RedactConnectionString(conn);
        Assert.DoesNotContain("p@ss;123", redactedConn);
        Assert.DoesNotContain("secretKey==", redactedConn);

        var mixed = "Authorization: Bearer abc.def.ghi client_secret=topsecret <PassWord>XmlSecret!</PassWord>";
        var redacted = Redactor.RedactSecrets(mixed);
        Assert.DoesNotContain("abc.def.ghi", redacted);
        Assert.DoesNotContain("topsecret", redacted);
        Assert.DoesNotContain("XmlSecret!", redacted);
    }

    [Fact]
    public async Task RestAndSoapAdapters_ThrowOnNonSuccessStatus_AndNormalizePaths()
    {
        using var errHandler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        using var rest = new RestV2SourceAdapter("http://ssrs.local/ReportServer", handlerOverride: errHandler);
        await Assert.ThrowsAsync<HttpRequestException>(() => rest.ListItemsAsync());

        using var soapErrHandler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        using var soap = new SoapSourceAdapter("http://ssrs.local/Reports/Pages/Folder.aspx", handlerOverride: soapErrHandler);
        await Assert.ThrowsAsync<HttpRequestException>(() => soap.ListItemsAsync());
        await Assert.ThrowsAsync<HttpRequestException>(() => soap.RenderAsync("/Sales/Summary", "PDF", new Dictionary<string, string>()));
        Assert.Contains(soapErrHandler.RequestedUris, u => u != null && u.ToString().StartsWith("http://ssrs.local/ReportServer?%2FSales%2FSummary", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(soapErrHandler.RequestedUris, u => u != null && u.ToString().Contains("/ReportServer/ReportServer", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TransformEngine_RejectsDtd_AndExtractsParametersAndAllFeatureTags()
    {
        var rulesPath = CompatibilityRulesLoader.ResolveRulesFilePath();
        var engine = new TransformEngine(new CompatibilityRulesLoader(rulesPath));

        var xxeRdl = Encoding.UTF8.GetBytes("<!DOCTYPE foo [<!ENTITY xxe SYSTEM \"file:///etc/passwd\">]><Report><Name>&xxe;</Name></Report>");
        var xxeResult = engine.TransformAndClassify(Guid.NewGuid(), xxeRdl);
        Assert.False(xxeResult.Success);

        var featureRdl = Encoding.UTF8.GetBytes(@"<?xml version=""1.0"" encoding=""utf-8""?>
<Report xmlns=""http://schemas.microsoft.com/sqlserver/reporting/2016/01/reportdefinition"">
  <ReportParameters>
    <ReportParameter Name=""Year""><DataType>Integer</DataType></ReportParameter>
    <ReportParameter Name=""Region""><DataType>String</DataType></ReportParameter>
  </ReportParameters>
  <DataSources>
    <DataSource Name=""FinanceDS""><DataSourceReference>/Shared/FinanceDS</DataSourceReference></DataSource>
  </DataSources>
  <DataSets>
    <DataSet Name=""SalesData""><SharedDataSet><SharedDataSetReference>/Shared/SalesSet</SharedDataSetReference></SharedDataSet></DataSet>
  </DataSets>
  <Body><ReportItems><Chart Name=""SalesChart"" /></ReportItems></Body>
</Report>");
        var res = engine.TransformAndClassify(Guid.NewGuid(), featureRdl);
        Assert.True(res.Success);
        Assert.Equal(2, res.ParameterCount);
        Assert.Contains("FinanceDS", res.DataSourceNames);
        Assert.Contains("/Shared/SalesSet", res.SharedDataSetReferences);
        Assert.Contains("Chart", res.FeatureTags);
    }

    [Fact]
    public async Task LocalFolderMigrationService_EnforcesAllowedRoots()
    {
        var allowedRoot = Path.Combine(_tempRoot, "allowed");
        var outsideRoot = Path.Combine(_tempRoot, "outside");
        Directory.CreateDirectory(allowedRoot);
        Directory.CreateDirectory(outsideRoot);

        var settings = new MigrationSettings { AllowedLocalFolderRoots = [allowedRoot] };
        var service = new LocalFolderMigrationService(NullLogger<LocalFolderMigrationService>.Instance, settings);

        var result = await service.RunMigrationAsync(new LocalFolderMigrationRequest
        {
            ServerUrl = "http://localhost:8090",
            ReportFolder = outsideRoot
        });

        Assert.False(result.Success);
        Assert.Contains("AllowedLocalFolderRoots", result.Message);
    }

    [Fact]
    public async Task DestinationClient_GetOrCreateCategoryAsync_ReturnsNullOnFailureInsteadOfSyntheticId()
    {
        using var failHandler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.BadGateway));
        using var client = new DestinationClient("http://boldreports.local/reporting/site/site1", "token", failHandler);

        var catId = await client.GetOrCreateCategoryAsync("site1", "Finance");
        Assert.Null(catId);
    }
}
