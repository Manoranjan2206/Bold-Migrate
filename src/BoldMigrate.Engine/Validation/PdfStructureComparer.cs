using System.Text;
using System.Text.RegularExpressions;

namespace BoldMigrate.Engine.Validation;

public class RenderComparisonResult
{
    /// <summary>Pages in the source server's PDF render, or null when it could not be determined.</summary>
    public int? SourcePageCount { get; set; }

    /// <summary>Pages in the destination server's PDF render, or null when it could not be determined.</summary>
    public int? DestinationPageCount { get; set; }

    public bool PageCountMatched => SourcePageCount.HasValue
                                    && DestinationPageCount.HasValue
                                    && SourcePageCount == DestinationPageCount;

    public long SourceBytes { get; set; }
    public long DestinationBytes { get; set; }

    public string? Summary { get; set; }
}

/// <summary>
/// Compares PDF renders structurally.
///
/// Deliberately does NOT produce a "visual similarity" percentage. SSRS and Bold Reports use
/// different PDF writers, so two byte streams for a visually identical report share almost no
/// bytes — any percentage derived from byte differences describes the PDF encoders, not the
/// report. A true pixel comparison needs a PDF rasterizer, which this project does not ship.
/// Page count is compared instead, because pagination overflow is a real and detectable
/// migration regression.
/// </summary>
public static class PdfStructureComparer
{
    private static readonly Regex PageObject = new(@"/Type\s*/Page[^s]", RegexOptions.Compiled);
    private static readonly Regex CountEntry = new(@"/Count\s+(\d+)", RegexOptions.Compiled);

    public static RenderComparisonResult Compare(byte[] sourcePdf, byte[] destPdf)
    {
        var result = new RenderComparisonResult
        {
            SourceBytes = sourcePdf?.LongLength ?? 0,
            DestinationBytes = destPdf?.LongLength ?? 0,
            SourcePageCount = CountPages(sourcePdf),
            DestinationPageCount = CountPages(destPdf)
        };

        if (result.SourcePageCount == null || result.DestinationPageCount == null)
        {
            result.Summary = "Page count could not be read from one of the PDF renders.";
        }
        else if (result.PageCountMatched)
        {
            result.Summary = $"Both servers produced {result.SourcePageCount} page(s).";
        }
        else
        {
            result.Summary = $"Pagination differs: SSRS produced {result.SourcePageCount} page(s), "
                             + $"Bold Reports produced {result.DestinationPageCount}.";
        }

        return result;
    }

    /// <summary>
    /// Reads the page count from a PDF. Scans the uncompressed structure first, then inflates
    /// any FlateDecode streams, because writers such as Bold Reports place the page tree inside
    /// a compressed object stream. Returns null when no page count can be determined.
    /// </summary>
    public static int? CountPages(byte[]? pdf)
    {
        if (pdf == null || pdf.Length < 5) return null;

        // Must start with the PDF magic number, otherwise this is not a PDF at all.
        if (!(pdf[0] == '%' && pdf[1] == 'P' && pdf[2] == 'D' && pdf[3] == 'F')) return null;

        // Latin1 keeps byte values intact for the structural keywords scanned for below.
        var text = Encoding.Latin1.GetString(pdf);

        var fromPlain = ExtractCount(text);
        if (fromPlain.HasValue) return fromPlain;

        // Page tree is likely inside a compressed object stream — inflate and retry.
        var inflated = InflateStreams(pdf);
        if (!string.IsNullOrEmpty(inflated))
        {
            var fromInflated = ExtractCount(inflated);
            if (fromInflated.HasValue) return fromInflated;
        }

        return null;
    }

    private static int? ExtractCount(string text)
    {
        // A /Count on the page tree root is the authoritative page total.
        var best = 0;
        foreach (Match m in CountEntry.Matches(text))
        {
            if (int.TryParse(m.Groups[1].Value, out var c) && c > best) best = c;
        }
        if (best > 0) return best;

        var pageObjects = PageObject.Matches(text).Count;
        return pageObjects > 0 ? pageObjects : null;
    }

    /// <summary>
    /// Inflates every FlateDecode stream found in the file and returns the concatenated text.
    /// Streams that fail to inflate are skipped — PDFs mix compressed and raw content.
    /// </summary>
    private static string InflateStreams(byte[] pdf)
    {
        var sb = new StringBuilder();
        var marker = Encoding.ASCII.GetBytes("stream");
        var endMarker = Encoding.ASCII.GetBytes("endstream");

        var pos = 0;
        var inflatedCount = 0;

        while (pos < pdf.Length && inflatedCount < 200)
        {
            var start = IndexOf(pdf, marker, pos);
            if (start < 0) break;

            var dataStart = start + marker.Length;
            // Skip the EOL that must follow the "stream" keyword.
            if (dataStart < pdf.Length && pdf[dataStart] == (byte)'\r') dataStart++;
            if (dataStart < pdf.Length && pdf[dataStart] == (byte)'\n') dataStart++;

            var end = IndexOf(pdf, endMarker, dataStart);
            if (end < 0) break;

            var length = end - dataStart;
            if (length > 0)
            {
                try
                {
                    using var input = new MemoryStream(pdf, dataStart, length, writable: false);
                    using var zlib = new System.IO.Compression.ZLibStream(input, System.IO.Compression.CompressionMode.Decompress);
                    using var output = new MemoryStream();
                    zlib.CopyTo(output, 16384);
                    if (output.Length > 0)
                    {
                        sb.Append(Encoding.Latin1.GetString(output.ToArray()));
                        inflatedCount++;
                    }
                }
                catch
                {
                    // Not a zlib stream (image data, already-raw content) — skip it.
                }
            }

            pos = end + endMarker.Length;
        }

        return sb.ToString();
    }

    private static int IndexOf(byte[] haystack, byte[] needle, int startAt)
    {
        for (var i = Math.Max(0, startAt); i <= haystack.Length - needle.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j]) { match = false; break; }
            }
            if (match) return i;
        }
        return -1;
    }
}
