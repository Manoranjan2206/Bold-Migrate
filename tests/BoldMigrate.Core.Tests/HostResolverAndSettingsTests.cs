using BoldMigrate.Core.Common;
using BoldMigrate.Core.Configuration;
using BoldMigrate.Core.Enums;
using BoldMigrate.Core.StateMachine;
using Xunit;

namespace BoldMigrate.Core.Tests;

public class HostResolverAndSettingsTests
{
    [Theory]
    [InlineData("http://localhost:8081/ReportServer", "host.docker.internal", "http://host.docker.internal:8081/ReportServer")]
    [InlineData("HTTP://LOCALHOST:8081/ReportServer", "host.docker.internal", "HTTP://host.docker.internal:8081/ReportServer")]
    [InlineData("https://127.0.0.1/ReportServer", "host.docker.internal", "https://host.docker.internal/ReportServer")]
    [InlineData("http://localhost.corp.internal:8081/ReportServer", "host.docker.internal", "http://localhost.corp.internal:8081/ReportServer")]
    [InlineData("https://reports.corp/Reports/localhost", "host.docker.internal", "https://reports.corp/Reports/localhost")]
    public void Resolve_OnlyReplacesLoopbackAuthorityCaseInsensitively(string url, string replacementHost, string expected)
    {
        var prev = Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER");
        try
        {
            Environment.SetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER", "true");
            var actual = HostResolver.Resolve(url);
            Assert.Equal(expected.Replace("host.docker.internal", replacementHost), actual);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER", prev);
        }
    }

    [Fact]
    public void ValidateAndNormalize_RejectsReservedShimPathsAndClampsBounds()
    {
        var reserved = new MigrationSettings { UrlShimListenPath = "/api/v2" };
        var (ok1, err1) = reserved.ValidateAndNormalize();
        Assert.False(ok1);
        Assert.NotNull(err1);

        var clamped = new MigrationSettings
        {
            ExtractionMaxParallel = 999,
            UploadMaxParallel = 0,
            ValidationDataAccuracyThreshold = 10m,
            ValidationSampleRowLimit = 99_999_999,
            ValidationRenderDpi = 72,
            UrlShimListenPath = "ReportServer/"
        };
        var (ok2, err2) = clamped.ValidateAndNormalize();
        Assert.True(ok2);
        Assert.Null(err2);
        Assert.Equal(16, clamped.ExtractionMaxParallel);
        Assert.Equal(1, clamped.UploadMaxParallel);
        Assert.Equal(90.0m, clamped.ValidationDataAccuracyThreshold);
        Assert.Equal(1_000_000, clamped.ValidationSampleRowLimit);
        Assert.Equal(96, clamped.ValidationRenderDpi);
        Assert.Equal("/ReportServer", clamped.UrlShimListenPath);
    }

    [Fact]
    public void ReportStateMachine_AllowsReUploadingUploadedReport()
    {
        Assert.True(ReportStateMachine.CanTransition(ReportState.Uploaded, ReportState.Uploading));
    }

    [Theory]
    [InlineData("Server=localhost\\SQLEXPRESS;Database=Sales;", null, false, true)]
    [InlineData("Server=.\\SQLEXPRESS;Database=Sales;", "SQL", false, true)]
    [InlineData("Data Source=(local);Initial Catalog=Sales;", "SQL", false, true)]
    [InlineData("Server=db.corp.local;Database=Sales;", "SQL", false, false)]
    [InlineData("Server=db.corp.local;Database=Sales;", "SQL", true, true)]
    [InlineData("Server=db.corp.local;Database=Sales;TrustServerCertificate=False;", "SQL", true, false)]
    public void SqlConnectionHelper_TrustsLoopbackOrExplicitOptInOnly(
        string connStr,
        string? ext,
        bool allowUntrusted,
        bool expected)
    {
        Assert.Equal(expected, SqlConnectionHelper.ShouldTrustSqlCertificate(connStr, ext, allowUntrusted));
    }

    [Fact]
    public void SqlConnectionHelper_DetectsCertificateChainError()
    {
        Assert.True(SqlConnectionHelper.IsUntrustedCertificateSqlError(-2146893019, "SSL Provider, error: 0 - Generic"));
        Assert.True(SqlConnectionHelper.IsUntrustedCertificateSqlError(0, "The certificate chain was issued by an authority that is not trusted."));
        Assert.False(SqlConnectionHelper.IsUntrustedCertificateSqlError(18456, "Login failed for user 'sa'."));
    }

    [Fact]
    public void SqlConnectionHelper_RemapsHostDatabaseAndTrustCert()
    {
        var updated = SqlConnectionHelper.RemapConnectionString(
            "Data Source=DEV-SQL01;Initial Catalog=Sales_Dev;Integrated Security=True",
            findServer: "DEV-SQL01",
            replaceServer: "PROD-SQL02.corp.local",
            findDatabase: "Sales_Dev",
            replaceDatabase: "Sales_Prod",
            trustServerCertificate: true);

        Assert.Contains("PROD-SQL02.corp.local", updated, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Sales_Prod", updated, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("TrustServerCertificate=True", updated, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DEV-SQL01", updated, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(null, "Skip")]
    [InlineData("skip", "Skip")]
    [InlineData("OVERWRITE", "Overwrite")]
    [InlineData("Rename", "Rename")]
    [InlineData("invalid", "Skip")]
    public void MigrationSettings_NormalizesConflictPolicy(string? input, string expected)
    {
        var s = new MigrationSettings { UploadConflictPolicy = input ?? string.Empty };
        var (ok, _) = s.ValidateAndNormalize();
        Assert.True(ok);
        Assert.Equal(expected, s.UploadConflictPolicy);
    }
}
