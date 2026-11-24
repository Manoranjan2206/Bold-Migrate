using System.Net;
using System.Text;
using BoldMigrate.Engine.Preview;

namespace BoldMigrate.Engine.Tests.Preview;

public class SharedDataSourceDefinitionTests
{
    // Shape returned by the SSRS SOAP GetItemDefinition and REST v2.0 content APIs.
    private const string ServerDefinition = """
        <?xml version="1.0" encoding="utf-8"?>
        <DataSourceDefinition xmlns="http://schemas.microsoft.com/sqlserver/reporting/2006/03/reportdatasource">
          <Extension>SQL</Extension>
          <ConnectString>Data Source=db01;Initial Catalog=Sales</ConnectString>
          <CredentialRetrieval>Store</CredentialRetrieval>
          <WindowsCredentials>False</WindowsCredentials>
          <UserName>report_reader</UserName>
          <Enabled>True</Enabled>
        </DataSourceDefinition>
        """;

    // Shape of a Visual Studio report project .rds file.
    private const string ProjectDefinition = """
        <?xml version="1.0" encoding="utf-8"?>
        <RptDataSource xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" Name="Warehouse">
          <ConnectionProperties>
            <Extension>SQLAZURE</Extension>
            <ConnectString>Data Source=wh.database.windows.net;Initial Catalog=WH</ConnectString>
            <IntegratedSecurity>true</IntegratedSecurity>
          </ConnectionProperties>
          <DataSourceID>4b0e0f8e-0000-0000-0000-000000000000</DataSourceID>
        </RptDataSource>
        """;

    private static byte[] Bytes(string xml) => Encoding.UTF8.GetBytes(xml);

    [Fact]
    public void Parses_report_server_definition()
    {
        var def = SharedDataSourceDefinition.Parse(Bytes(ServerDefinition))!;

        Assert.Equal("SQL", def.Extension);
        Assert.Equal("Data Source=db01;Initial Catalog=Sales", def.ConnectString);
        Assert.Equal("Store", def.CredentialRetrieval);
        Assert.Equal("report_reader", def.UserName);
        Assert.True(def.Enabled);
    }

    [Fact]
    public void Parses_project_rds_and_maps_extension_alias()
    {
        var def = SharedDataSourceDefinition.Parse(Bytes(ProjectDefinition))!;

        Assert.Equal("SQL", def.Extension);
        Assert.Equal("Integrated", def.CredentialRetrieval);
    }

    [Fact]
    public void Rejects_non_data_source_and_dtd_content()
    {
        Assert.Null(SharedDataSourceDefinition.Parse(Bytes("<Report />")));
        Assert.Null(SharedDataSourceDefinition.Parse(Bytes("not xml")));

        const string xxe = """
            <?xml version="1.0"?>
            <!DOCTYPE d [ <!ENTITY x SYSTEM "file:///c:/windows/win.ini"> ]>
            <DataSourceDefinition><ConnectString>&x;</ConnectString></DataSourceDefinition>
            """;
        Assert.Null(SharedDataSourceDefinition.Parse(Bytes(xxe)));
    }

    [Fact]
    public void Step1_credentials_win_and_are_stored()
    {
        var def = SharedDataSourceDefinition.Parse(Bytes(ServerDefinition))!;

        var effective = def.WithCredentials(new NetworkCredential("svc_reports", "s3cret", "CORP"));

        Assert.Equal("Store", effective.CredentialRetrieval);
        Assert.Equal("CORP\\svc_reports", effective.UserName);
        Assert.Equal("s3cret", effective.Password);
    }

    [Fact]
    public void Stored_credentials_without_a_password_fall_back_to_prompt()
    {
        // SSRS never exports the stored password, so "Store" cannot be honoured as-is.
        var def = SharedDataSourceDefinition.Parse(Bytes(ServerDefinition))!;

        var effective = def.WithCredentials(null);

        Assert.Equal("Prompt", effective.CredentialRetrieval);
        Assert.Null(effective.Password);
        Assert.False(string.IsNullOrWhiteSpace(effective.Prompt));
    }

    [Fact]
    public void Integrated_is_kept_when_no_credentials_were_entered()
    {
        var def = SharedDataSourceDefinition.Parse(Bytes(ProjectDefinition))!;

        Assert.Equal("Integrated", def.WithCredentials(null).CredentialRetrieval);
    }

    [Fact]
    public void WithCredentials_does_not_mutate_the_parsed_definition()
    {
        var def = SharedDataSourceDefinition.Parse(Bytes(ServerDefinition))!;

        def.WithCredentials(new NetworkCredential("other", "pw"));

        Assert.Equal("report_reader", def.UserName);
        Assert.Null(def.Password);
    }
}
