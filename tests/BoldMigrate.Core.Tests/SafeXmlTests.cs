using BoldMigrate.Core.Common;
using Xunit;

namespace BoldMigrate.Core.Tests;

public class SafeXmlTests
{
    private const string XxePayload =
        "<?xml version=\"1.0\"?>" +
        "<!DOCTYPE Report [<!ENTITY xxe SYSTEM \"file:///C:/Windows/win.ini\">]>" +
        "<Report><Description>&xxe;</Description></Report>";

    private const string BillionLaughs =
        "<?xml version=\"1.0\"?>" +
        "<!DOCTYPE lolz [<!ENTITY lol \"lol\">" +
        "<!ENTITY lol2 \"&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;\">]>" +
        "<lolz>&lol2;</lolz>";

    [Fact]
    public void Parse_RejectsExternalEntityDeclaration()
    {
        Assert.ThrowsAny<System.Xml.XmlException>(() => SafeXml.Parse(XxePayload));
    }

    [Fact]
    public void Parse_RejectsEntityExpansionPayload()
    {
        Assert.ThrowsAny<System.Xml.XmlException>(() => SafeXml.Parse(BillionLaughs));
    }

    [Fact]
    public void LoadBytes_RejectsDtdInRdlBytes()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(XxePayload);

        Assert.ThrowsAny<System.Xml.XmlException>(() => SafeXml.LoadBytes(bytes));
    }

    [Fact]
    public void Parse_StillReadsOrdinaryRdl()
    {
        const string rdl =
            "<Report xmlns=\"http://schemas.microsoft.com/sqlserver/reporting/2016/01/reportdefinition\">" +
            "<ReportParameters><ReportParameter Name=\"Year\" /></ReportParameters></Report>";

        var doc = SafeXml.Parse(rdl);

        Assert.Equal("Report", doc.Root?.Name.LocalName);
        Assert.Single(doc.Descendants().Where(e => e.Name.LocalName == "ReportParameter"));
    }

    [Fact]
    public void LoadFile_RejectsDtdFromDisk()
    {
        var path = Path.Combine(Path.GetTempPath(), $"BoldMigrate_SafeXml_{Guid.NewGuid():N}.rdl");
        File.WriteAllText(path, XxePayload);
        try
        {
            Assert.ThrowsAny<System.Xml.XmlException>(() => SafeXml.LoadFile(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
