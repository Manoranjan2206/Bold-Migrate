using System.Text;
using BoldMigrate.Engine.Preview;

namespace BoldMigrate.Engine.Tests.Preview;

public class ReportDependenciesTests
{
    private const string Rdl = """
        <?xml version="1.0" encoding="utf-8"?>
        <Report xmlns="http://schemas.microsoft.com/sqlserver/reporting/2016/01/reportdefinition">
          <DataSources>
            <DataSource Name="Warehouse"><DataSourceReference>/Data Sources/Warehouse</DataSourceReference></DataSource>
            <DataSource Name="Inline"><ConnectionProperties><DataProvider>SQL</DataProvider><ConnectString>x</ConnectString></ConnectionProperties></DataSource>
          </DataSources>
          <DataSets>
            <DataSet Name="Orders"><SharedDataSet><SharedDataSetReference>Orders</SharedDataSetReference></SharedDataSet></DataSet>
            <DataSet Name="Local"><Query><DataSourceName>Inline</DataSourceName><CommandText>select 1</CommandText></Query></DataSet>
          </DataSets>
          <ReportSections><ReportSection><Body><ReportItems>
            <Subreport Name="Detail"><ReportName>Order Detail</ReportName></Subreport>
          </ReportItems></Body></ReportSection></ReportSections>
        </Report>
        """;

    [Fact]
    public void Reads_shared_and_embedded_dependencies()
    {
        var deps = ReportDependencies.Read(Encoding.UTF8.GetBytes(Rdl));

        Assert.Equal(2, deps.DataSources.Count);
        Assert.Equal("/Data Sources/Warehouse", deps.DataSources.Single(d => d.Name == "Warehouse").SharedReference);
        Assert.Null(deps.DataSources.Single(d => d.Name == "Inline").SharedReference);
        Assert.Equal("Orders", Assert.Single(deps.SharedDataSets).SharedReference);
        Assert.Equal("Order Detail", Assert.Single(deps.Subreports));
    }

    [Fact]
    public void Recognises_report_definitions_only()
    {
        Assert.True(ReportDependencies.IsReportDefinition(Encoding.UTF8.GetBytes(Rdl)));
        Assert.False(ReportDependencies.IsReportDefinition(Encoding.UTF8.GetBytes("<DataSourceDefinition />")));
        Assert.False(ReportDependencies.IsReportDefinition(Encoding.UTF8.GetBytes("{ \"json\": true }")));
    }

    [Fact]
    public void Reads_the_data_source_of_a_shared_dataset()
    {
        const string rsd = """
            <SharedDataSet xmlns="http://schemas.microsoft.com/sqlserver/reporting/2010/01/shareddatasetdefinition">
              <DataSet Name=""><Query><DataSourceReference>Warehouse</DataSourceReference><CommandText>select 1</CommandText></Query></DataSet>
            </SharedDataSet>
            """;

        Assert.Equal("Warehouse", ReportDependencies.ReadDataSetDataSourceReference(Encoding.UTF8.GetBytes(rsd)));
    }
}
