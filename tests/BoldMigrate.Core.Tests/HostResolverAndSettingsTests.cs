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
}
