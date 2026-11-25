using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BoldMigrate.Api.Middleware;
using BoldMigrate.Core.Enums;
using BoldMigrate.Core.Interfaces;
using BoldMigrate.Core.Models;
using BoldMigrate.Infrastructure.Credentials;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BoldMigrate.Api.Tests;

public sealed class BoldMigrateApiFactory : WebApplicationFactory<Program>, IDisposable
{
    public string TempRoot { get; } = Path.Combine(Path.GetTempPath(), "BoldMigrate_ApiTests_" + Guid.NewGuid().ToString("N"));
    public string? OverrideApiAccessToken { get; set; }

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        var dataDir = Path.Combine(TempRoot, "data");
        var stagingDir = Path.Combine(TempRoot, "staging");
        var logsDir = Path.Combine(TempRoot, "logs");
        Directory.CreateDirectory(dataDir);
        Directory.CreateDirectory(stagingDir);
        Directory.CreateDirectory(logsDir);

        builder.UseSetting("BoldMigrate:DatabasePath", Path.Combine(dataDir, "test.db"));
        builder.UseSetting("BoldMigrate:StagingRootPath", stagingDir);
        builder.UseSetting("BoldMigrate:LogsPath", logsDir);
        builder.UseSetting("BoldMigrate:UrlShimEnabled", "true");
        builder.UseSetting("BoldMigrate:UrlShimListenPath", "/ReportServer");
        if (!string.IsNullOrEmpty(OverrideApiAccessToken))
        {
            builder.UseSetting("BoldMigrate:ApiAccessToken", OverrideApiAccessToken);
        }
    }

    public new void Dispose()
    {
        base.Dispose();
        try
        {
            if (Directory.Exists(TempRoot))
                Directory.Delete(TempRoot, recursive: true);
        }
        catch { }
    }
}

public class ApiIntegrationTests
{
    [Fact]
    public async Task SecurityHeaders_And_CsrfProtection_AreEnforced()
    {
        using var factory = new BoldMigrateApiFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        var healthRes = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, healthRes.StatusCode);
        Assert.Equal("nosniff", healthRes.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("SAMEORIGIN", healthRes.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("no-referrer", healthRes.Headers.GetValues("Referrer-Policy").Single());

        // Cross-site browser POST without CSRF header must be blocked with 403
        using var crossSiteReq = new HttpRequestMessage(HttpMethod.Post, "/api/migration/runs")
        {
            Content = JsonContent.Create(new { name = "Blocked", sourceUrl = "http://ssrs.local/ReportServer" })
        };
        crossSiteReq.Headers.Add("Origin", "https://evil.example");
        crossSiteReq.Headers.Add("Sec-Fetch-Site", "cross-site");
        var blocked = await client.SendAsync(crossSiteReq);
        Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);

        // Request with matching CSRF cookie + X-BoldMigrate-Csrf header succeeds
        var csrfValue = "test-csrf-token-value";
        using var validReq = new HttpRequestMessage(HttpMethod.Post, "/api/migration/runs")
        {
            Content = JsonContent.Create(new { name = "Valid", sourceUrl = "http://ssrs.local/ReportServer" })
        };
        validReq.Headers.Add("Cookie", $"{SecurityMiddleware.CsrfCookieName}={csrfValue}");
        validReq.Headers.Add(SecurityMiddleware.CsrfHeaderName, csrfValue);
        validReq.Headers.Add("Origin", "http://localhost");
        var ok = await client.SendAsync(validReq);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }

    [Fact]
    public async Task ApiAccessToken_WhenConfigured_RejectsUnauthenticatedApiCalls()
    {
        using var factory = new BoldMigrateApiFactory { OverrideApiAccessToken = "secret-operator-token-123" };
        using var client = factory.CreateClient();

        var unauth = await client.GetAsync("/api/settings");
        Assert.Equal(HttpStatusCode.Unauthorized, unauth.StatusCode);

        using var authReq = new HttpRequestMessage(HttpMethod.Get, "/api/settings");
        authReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "secret-operator-token-123");
        var authed = await client.SendAsync(authReq);
        Assert.Equal(HttpStatusCode.OK, authed.StatusCode);
    }

    [Fact]
    public async Task SettingsController_RejectsReservedShimPath_And_LoadsRules()
    {
        using var factory = new BoldMigrateApiFactory();
        using var client = factory.CreateClient();

        var badRes = await client.PutAsJsonAsync("/api/settings", new Dictionary<string, object>
        {
            ["UrlShim.ListenPath"] = "/api",
            ["UrlShim.Enabled"] = true,
            ["Validation.DataAccuracyThreshold"] = 99.5,
            ["Validation.SampleRowLimit"] = 10000,
            ["Extraction.MaxParallel"] = 4,
            ["Upload.MaxParallel"] = 4
        });
        Assert.Equal(HttpStatusCode.BadRequest, badRes.StatusCode);

        var rulesRes = await client.GetAsync("/api/settings/rules");
        Assert.Equal(HttpStatusCode.OK, rulesRes.StatusCode);
        var rulesJson = await rulesRes.Content.ReadAsStringAsync();
        Assert.Contains("rules", rulesJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ScannerController_RejectsUnsupportedExtension()
    {
        using var factory = new BoldMigrateApiFactory();
        using var client = factory.CreateClient();

        using var content = new MultipartFormDataContent();
        using var fileContent = new ByteArrayContent(Encoding.UTF8.GetBytes("not an rdl"));
        content.Add(fileContent, "file", "payload.exe");

        var res = await client.PostAsync("/api/scanner/analyse", content);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task MigrationController_EnforcesSrc007_RedactsSecrets_AndHidesBearerJwtOnDesignerConfig()
    {
        using var factory = new BoldMigrateApiFactory();
        using var client = factory.CreateClient();

        var createRes = await client.PostAsJsonAsync("/api/migration/runs", new
        {
            name = "SecurityRun",
            sourceUrl = "http://ssrs.corp.local/ReportServer",
            forceNew = true
        });
        Assert.Equal(HttpStatusCode.OK, createRes.StatusCode);
        var run = await createRes.Content.ReadFromJsonAsync<MigrationRun>();
        Assert.NotNull(run);

        // 1. Basic auth over plain non-loopback HTTP must return SRC-007
        var src007Res = await client.PostAsJsonAsync($"/api/migration/runs/{run!.Id}/connect", new
        {
            sourceUrl = "http://ssrs.corp.local/ReportServer",
            username = "admin",
            password = "SecretPassword1!",
            authMode = "Basic"
        });
        Assert.Equal(HttpStatusCode.BadRequest, src007Res.StatusCode);
        var src007Body = await src007Res.Content.ReadAsStringAsync();
        Assert.Contains("SRC-007", src007Body);

        // 2. Seed staged data source with secret password + destination token, verify redaction & zero JWT exposure
        using (var scope = factory.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IMigrationStore>();
            var staging = scope.ServiceProvider.GetRequiredService<IStagingStore>();
            var credStore = scope.ServiceProvider.GetRequiredService<ICredentialStore>();

            credStore.SetDestinationToken("super-secret-jwt-token");
            run.DestinationServerUrl = "http://boldreports.local:8090/reporting/site/site1";
            await store.UpdateRunAsync(run);

            var dsXml = Encoding.UTF8.GetBytes(@"<?xml version=""1.0"" encoding=""utf-8""?>
<RptDataSource xmlns=""http://schemas.microsoft.com/sqlserver/reporting/2010/01/dataourcedefinition"">
  <ConnectionProperties>
    <Extension>SQL</Extension>
    <ConnectString>Server=sql.local;Database=Sales;User Id=sa;Password=TopSecretSqlPass!</ConnectString>
  </ConnectionProperties>
</RptDataSource>");
            var stagedPath = await staging.StageFileAsync(run.Id, "/DataSources/SalesDS.rsds", dsXml);

            await store.AddCatalogItemsAsync([
                new CatalogItem
                {
                    Id = Guid.NewGuid(),
                    RunId = run.Id,
                    ItemType = ItemType.DataSource,
                    Name = "SalesDS",
                    SourcePath = "/DataSources/SalesDS",
                    ParentPath = "/DataSources",
                    StagedFilePath = stagedPath
                }
            ]);
        }

        var dsRes = await client.GetAsync($"/api/migration/runs/{run.Id}/datasources");
        Assert.Equal(HttpStatusCode.OK, dsRes.StatusCode);
        var dsBody = await dsRes.Content.ReadAsStringAsync();
        Assert.DoesNotContain("TopSecretSqlPass!", dsBody);

        var designerRes = await client.GetAsync($"/api/migration/runs/{run.Id}/designer-config");
        Assert.Equal(HttpStatusCode.OK, designerRes.StatusCode);
        var designerRaw = await designerRes.Content.ReadAsStringAsync();
        using var designerDoc = JsonDocument.Parse(designerRaw);
        Assert.Equal(string.Empty, designerDoc.RootElement.GetProperty("serviceAuthorizationToken").GetString());
        Assert.True(designerDoc.RootElement.GetProperty("hasAuthorizationToken").GetBoolean());
        Assert.DoesNotContain("super-secret-jwt-token", designerRaw);

        // 3. DeltaSync on unknown run returns 404 (not unhandled KeyNotFoundException 500)
        var missingDelta = await client.PostAsync($"/api/migration/runs/{Guid.NewGuid()}/delta-sync", null);
        Assert.Equal(HttpStatusCode.NotFound, missingDelta.StatusCode);
    }

    [Fact]
    public async Task UrlShim_BlocksUnsafeExternalRedirects_And_AllowsSameOrigin()
    {
        using var factory = new BoldMigrateApiFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using (var scope = factory.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IMigrationStore>();
            var run = new MigrationRun
            {
                Id = Guid.NewGuid(),
                Name = "ShimTest",
                Status = RunStatus.CutOver,
                SourceServerUrl = "http://ssrs.local/ReportServer",
                DestinationServerUrl = "https://reports.corp.local/reporting/site/site1",
                CreatedUtc = DateTime.UtcNow
            };
            await store.CreateRunAsync(run);
            await store.AddUrlMappingsAsync([
                new UrlMapping
                {
                    Id = Guid.NewGuid(),
                    RunId = run.Id,
                    SourcePath = "/Finance/Unsafe",
                    DestinationUrl = "https://attacker.example/phish"
                },
                new UrlMapping
                {
                    Id = Guid.NewGuid(),
                    RunId = run.Id,
                    SourcePath = "/Finance/Safe",
                    DestinationUrl = "https://reports.corp.local/reporting/site/site1/reports/123"
                }
            ]);
        }

        var unsafeRes = await client.GetAsync("/ReportServer?/Finance/Unsafe");
        Assert.Equal(HttpStatusCode.BadRequest, unsafeRes.StatusCode);

        var safeRes = await client.GetAsync("/ReportServer?/Finance/Safe&Year=2026");
        Assert.Equal(HttpStatusCode.Redirect, safeRes.StatusCode);
        Assert.Equal("https://reports.corp.local/reporting/site/site1/reports/123?Year=2026", safeRes.Headers.Location?.ToString());
    }
}
