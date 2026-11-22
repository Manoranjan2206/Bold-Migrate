namespace BoldMigrate.Engine.Validation;

public class VisualComparisonResult
{
    public decimal SimilarityPercentage { get; set; } = 100.0m;
    public int DifferingPixels { get; set; }
    public int TotalPixels { get; set; }
    public int LayoutShifts { get; set; }
    public bool PageCountMatched { get; set; } = true;
    public string? DiffSummary { get; set; }
}

public static class PixelComparer
{
    public static VisualComparisonResult CompareBytes(byte[] sourceRender, byte[] destRender, int pixelTolerance = 2)
    {
        var result = new VisualComparisonResult();

        if (sourceRender.Length == 0 && destRender.Length == 0)
        {
            result.SimilarityPercentage = 100.0m;
            return result;
        }

        if (sourceRender.Length == 0 || destRender.Length == 0)
        {
            result.SimilarityPercentage = 0.0m;
            result.PageCountMatched = false;
            result.DiffSummary = "One of the renders produced no data.";
            return result;
        }

        // For raw byte / pixel comparison:
        var totalBytes = Math.Max(sourceRender.Length, destRender.Length);
        result.TotalPixels = totalBytes;

        var minBytes = Math.Min(sourceRender.Length, destRender.Length);
        var diffCount = Math.Abs(sourceRender.Length - destRender.Length);

        var contiguousDiffs = 0;
        var inShift = false;

        for (var i = 0; i < minBytes; i++)
        {
            var diff = Math.Abs(sourceRender[i] - destRender[i]);
            if (diff > pixelTolerance)
            {
                diffCount++;
                if (!inShift)
                {
                    inShift = true;
                    contiguousDiffs++;
                }
            }
            else
            {
                inShift = false;
            }
        }

        result.DifferingPixels = diffCount;
        result.LayoutShifts = contiguousDiffs;

        result.SimilarityPercentage = totalBytes > 0
            ? Math.Max(0, Math.Round((1.0m - ((decimal)diffCount / totalBytes)) * 100.0m, 1))
            : 100.0m;

        return result;
    }
}
