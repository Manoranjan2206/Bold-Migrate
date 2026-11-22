using BoldMigrate.Engine.Logging;
using Xunit;

namespace BoldMigrate.Engine.Tests.Logging;

public class RedactorTests
{
    [Fact]
    public void RedactConnectionString_SqlServer_RedactsPassword()
    {
        var input = "Server=tcp:sqlserver.corp.local;Database=FinanceDb;User Id=report_reader;Password=SecretP@ssw0rd!;TrustServerCertificate=True;";
        var result = Redactor.RedactConnectionString(input);

        Assert.DoesNotContain("SecretP@ssw0rd!", result);
        Assert.Contains("Password=[redacted]", result);
        Assert.Contains("User Id=[redacted]", result);
        Assert.Contains("Server=tcp:sqlserver.corp.local", result);
    }

    [Fact]
    public void RedactConnectionString_Oracle_RedactsPassword()
    {
        var input = "Data Source=sales_oracle;User Id=scott;Password=tiger;";
        var result = Redactor.RedactConnectionString(input);

        Assert.DoesNotContain("tiger", result);
        Assert.Contains("Password=[redacted]", result);
        Assert.Contains("User Id=[redacted]", result);
    }

    [Fact]
    public void RedactConnectionString_Odbc_RedactsUidAndPwd()
    {
        var input = "Driver={ODBC Driver 17 for SQL Server};Server=myServerAddress;Database=myDataBase;Uid=myUsername;Pwd=myPassword;";
        var result = Redactor.RedactConnectionString(input);

        Assert.DoesNotContain("myPassword", result);
        Assert.Contains("Pwd=[redacted]", result);
        Assert.Contains("Uid=[redacted]", result);
    }

    [Fact]
    public void RedactSecrets_BearerToken_ShowsFirstFourChars()
    {
        var input = "Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0";
        var result = Redactor.RedactSecrets(input);

        Assert.StartsWith("Bearer eyJh[redacted]", result);
    }

    [Fact]
    public void RedactAuthHeader_ReturnsSchemeOnly()
    {
        var header = "Basic dXNlcjpwYXNzd29yZA==";
        var result = Redactor.RedactAuthHeader(header);

        Assert.Equal("Basic", result);
    }
}
