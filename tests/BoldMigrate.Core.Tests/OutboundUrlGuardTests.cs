using BoldMigrate.Core.Common;
using Xunit;

namespace BoldMigrate.Core.Tests;

public class OutboundUrlGuardTests
{
    [Theory]
    // The classic SSRF payoff: on Windows the source adapters fall back to the process's
    // Windows identity, so these must never be reachable from an operator-supplied URL.
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://169.254.169.254")]
    [InlineData("http://metadata.google.internal/computeMetadata/v1/")]
    [InlineData("http://[::ffff:169.254.169.254]/latest/")]
    public void Check_BlocksLinkLocalAndMetadataEndpoints(string url)
    {
        var result = OutboundUrlGuard.Check(url);

        Assert.False(result.Allowed);
        Assert.False(string.IsNullOrWhiteSpace(result.Reason));
    }

    [Theory]
    // Bold Migrate legitimately targets internal and loopback report servers, so these must pass.
    [InlineData("http://localhost/ReportServer")]
    [InlineData("http://127.0.0.1:8080/Reports")]
    [InlineData("https://ssrs.corp.internal/ReportServer")]
    [InlineData("http://10.1.2.3/Reports")]
    public void Check_AllowsNormalReportServerTargets(string url)
    {
        var result = OutboundUrlGuard.Check(url);

        Assert.True(result.Allowed);
        Assert.Null(result.Reason);
    }

    [Theory]
    [InlineData("file:///C:/windows/win.ini")]
    [InlineData("ftp://ssrs.corp.internal/")]
    [InlineData("not-a-url")]
    [InlineData("")]
    [InlineData(null)]
    public void Check_RejectsNonHttpSchemesAndMalformedInput(string? url)
    {
        var result = OutboundUrlGuard.Check(url);

        Assert.False(result.Allowed);
    }

    [Fact]
    public void Check_WhenAllowlistConfigured_RejectsHostsOutsideIt()
    {
        string[] allowed = ["ssrs.corp.internal", "reports.corp.internal"];

        Assert.True(OutboundUrlGuard.Check("https://ssrs.corp.internal/ReportServer", allowed).Allowed);
        Assert.True(OutboundUrlGuard.Check("https://REPORTS.corp.internal/x", allowed).Allowed);

        var denied = OutboundUrlGuard.Check("https://attacker.example.com/", allowed);
        Assert.False(denied.Allowed);
        Assert.Contains("AllowedSourceHosts", denied.Reason);
    }

    [Fact]
    public void Check_WhenAllowlistEmpty_DoesNotRestrictOrdinaryHosts()
    {
        var result = OutboundUrlGuard.Check("https://anything.example.com/", []);

        Assert.True(result.Allowed);
    }
}
