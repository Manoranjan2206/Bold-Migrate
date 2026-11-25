using System.Text;
using BoldMigrate.Core.Enums;
using BoldMigrate.Engine.Transform;
using Xunit;

namespace BoldMigrate.Engine.Tests.Transform;

public class TransformEngineTests
{
    private readonly TransformEngine _engine;

    public TransformEngineTests()
    {
        var loader = new CompatibilityRulesLoader();
        _engine = new TransformEngine(loader);
    }

    [Fact]
    public void Transform_StripsUtf8Bom()
    {
        var xml = "<Report xmlns=\"http://schemas.microsoft.com/sqlserver/reporting/2016/01/reportdefinition\" />";
        var xmlBytes = Encoding.UTF8.GetBytes(xml);
        var bomBytes = new byte[xmlBytes.Length + 3];
        bomBytes[0] = 0xEF; bomBytes[1] = 0xBB; bomBytes[2] = 0xBF;
        Array.Copy(xmlBytes, 0, bomBytes, 3, xmlBytes.Length);

        var result = _engine.TransformAndClassify(Guid.NewGuid(), bomBytes);

        Assert.True(result.Success);
        Assert.Contains(result.Actions, a => a.ActionCode == "TR-STRIP-BOM");
    }

    [Fact]
    public void Transform_RebindsDataSourceReference()
    {
        var xml = @"<Report xmlns=""http://schemas.microsoft.com/sqlserver/reporting/2016/01/reportdefinition"">
  <DataSources>
    <DataSource Name=""SalesDS"">
      <DataSourceReference>/Shared/SalesDataSource</DataSourceReference>
    </DataSource>
  </DataSources>
</Report>";

        var mappings = new Dictionary<string, string>
        {
            ["/Shared/SalesDataSource"] = "dest-ds-123"
        };

        var result = _engine.TransformAndClassify(Guid.NewGuid(), Encoding.UTF8.GetBytes(xml), dataSourceMappings: mappings);

        Assert.True(result.Success);
        Assert.Contains(result.Actions, a => a.ActionCode == "TR-REBIND-DS" && a.After == "dest-ds-123");
        var rewrittenXml = Encoding.UTF8.GetString(result.RewrittenBytes);
        Assert.Contains("dest-ds-123", rewrittenXml);
    }

    [Fact]
    public void Transform_CustomAssembly_ClassifiedAsNeedsReview()
    {
        var xml = @"<Report xmlns=""http://schemas.microsoft.com/sqlserver/reporting/2016/01/reportdefinition"">
  <CodeModules>
    <CodeModule>CustomReportingLib.Security, Version=1.0.0.0</CodeModule>
  </CodeModules>
</Report>";

        var result = _engine.TransformAndClassify(Guid.NewGuid(), Encoding.UTF8.GetBytes(xml));

        Assert.Equal(ReportBucket.NeedsReview, result.Bucket);
        Assert.Equal("CF-ASSEMBLY", result.BlockReasonCode);
    }

    [Fact]
    public void Transform_Subreport_ClassifiedWithFeatureTag()
    {
        var xml = @"<Report xmlns=""http://schemas.microsoft.com/sqlserver/reporting/2016/01/reportdefinition"">
  <Body>
    <ReportItems>
      <Subreport Name=""Sub1"">
        <ReportName>/Finance/Details</ReportName>
      </Subreport>
    </ReportItems>
  </Body>
</Report>";

        var result = _engine.TransformAndClassify(Guid.NewGuid(), Encoding.UTF8.GetBytes(xml));

        Assert.Contains("Subreport", result.FeatureTags);
        Assert.NotEqual(ReportBucket.Blocked, result.Bucket);
    }
}
