using System.IO.Compression;
using System.Text;
using BoldMigrate.Engine.Validation;
using Xunit;

namespace BoldMigrate.Engine.Tests.Validation;

public class PdfStructureComparerTests
{
    private static byte[] UncompressedPdf(int pageCount)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.5\n");
        sb.Append("1 0 obj\n<<\n/Type /Catalog\n/Pages 2 0 R\n>>\nendobj\n");
        sb.Append($"2 0 obj\n<<\n/Type /Pages\n/Count {pageCount}\n>>\nendobj\n");
        sb.Append("%%EOF");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    private static byte[] CompressedPdf(int pageCount)
    {
        // Mirrors writers that put the page tree inside a FlateDecode object stream.
        var inner = Encoding.Latin1.GetBytes($"<<\n/Type /Pages\n/Count {pageCount}\n>>");
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(inner, 0, inner.Length);
        }

        var payload = compressed.ToArray();
        using var pdf = new MemoryStream();
        var header = Encoding.Latin1.GetBytes("%PDF-1.5\n1 0 obj\n<<\n/Type /ObjStm\n/Filter /FlateDecode\n>>\nstream\n");
        pdf.Write(header, 0, header.Length);
        pdf.Write(payload, 0, payload.Length);
        var footer = Encoding.Latin1.GetBytes("\nendstream\nendobj\n%%EOF");
        pdf.Write(footer, 0, footer.Length);
        return pdf.ToArray();
    }

    [Fact]
    public void CountPages_ReadsCountFromUncompressedPdf()
    {
        Assert.Equal(7, PdfStructureComparer.CountPages(UncompressedPdf(7)));
    }

    [Fact]
    public void CountPages_ReadsCountFromCompressedObjectStream()
    {
        Assert.Equal(3, PdfStructureComparer.CountPages(CompressedPdf(3)));
    }

    [Fact]
    public void CountPages_ReturnsNullForNonPdfBytes()
    {
        Assert.Null(PdfStructureComparer.CountPages(Encoding.ASCII.GetBytes("Name,Value\nA,1\n")));
        Assert.Null(PdfStructureComparer.CountPages(Array.Empty<byte>()));
        Assert.Null(PdfStructureComparer.CountPages(null));
    }

    [Fact]
    public void Compare_FlagsPaginationMismatch()
    {
        var result = PdfStructureComparer.Compare(UncompressedPdf(2), UncompressedPdf(5));

        Assert.Equal(2, result.SourcePageCount);
        Assert.Equal(5, result.DestinationPageCount);
        Assert.False(result.PageCountMatched);
        Assert.Contains("Pagination differs", result.Summary);
    }

    [Fact]
    public void Compare_ReportsMatchWhenPageCountsAgree()
    {
        var result = PdfStructureComparer.Compare(UncompressedPdf(4), CompressedPdf(4));

        Assert.True(result.PageCountMatched);
        Assert.Contains("4 page(s)", result.Summary);
    }

    [Fact]
    public void Compare_ReportsUnreadableWhenOneSideIsNotAPdf()
    {
        var result = PdfStructureComparer.Compare(UncompressedPdf(1), Encoding.ASCII.GetBytes("not a pdf"));

        Assert.Equal(1, result.SourcePageCount);
        Assert.Null(result.DestinationPageCount);
        Assert.False(result.PageCountMatched);
        Assert.Contains("could not be read", result.Summary);
    }
}
