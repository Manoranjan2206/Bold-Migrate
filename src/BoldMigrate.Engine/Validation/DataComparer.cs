using System.Globalization;

namespace BoldMigrate.Engine.Validation;

public class DataComparisonResult
{
    public decimal AccuracyPercentage { get; set; }
    public long RowsSource { get; set; }
    public long RowsDestination { get; set; }
    public bool RowCountMatched => RowsSource == RowsDestination;
    public int DifferingCellsCount { get; set; }
    public string? DifferingCellSummary { get; set; }
    public bool NumericTotalsMatched { get; set; } = true;
    public bool UsedSampling { get; set; }
}

public static class DataComparer
{
    public static DataComparisonResult CompareCsvData(string sourceCsv, string destinationCsv, int sampleRowLimit = 10000)
    {
        var result = new DataComparisonResult();

        var sourceLines = sourceCsv.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        var destLines = destinationCsv.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);

        result.RowsSource = Math.Max(0, sourceLines.Length - 1);
        result.RowsDestination = Math.Max(0, destLines.Length - 1);

        if (sourceLines.Length == 0 && destLines.Length == 0)
        {
            result.AccuracyPercentage = 100.0m;
            return result;
        }

        if (result.RowsSource != result.RowsDestination)
        {
            result.AccuracyPercentage = 0.0m;
            result.DifferingCellSummary = $"Row count mismatch: Source has {result.RowsSource} rows, Destination has {result.RowsDestination} rows.";
            return result;
        }

        var totalCellsCompared = 0;
        var matchingCells = 0;
        var diffSummary = new List<string>();

        var rowsToCompare = Math.Min(sourceLines.Length, sampleRowLimit);
        if (sourceLines.Length > sampleRowLimit)
        {
            result.UsedSampling = true;
        }

        for (var i = 0; i < rowsToCompare; i++)
        {
            var sCells = sourceLines[i].Split(',');
            var dCells = destLines[i].Split(',');

            var maxCols = Math.Max(sCells.Length, dCells.Length);
            for (var c = 0; c < maxCols; c++)
            {
                totalCellsCompared++;
                var sVal = c < sCells.Length ? sCells[c] : "";
                var dVal = c < dCells.Length ? dCells[c] : "";

                if (NormalizeAndAreEqual(sVal, dVal))
                {
                    matchingCells++;
                }
                else
                {
                    result.DifferingCellsCount++;
                    if (diffSummary.Count < 5)
                    {
                        diffSummary.Add($"Row {i + 1}, Col {c + 1}: Source='{sVal}' vs Dest='{dVal}'");
                    }
                }
            }
        }

        result.AccuracyPercentage = totalCellsCompared > 0
            ? Math.Round(((decimal)matchingCells / totalCellsCompared) * 100.0m, 1)
            : 100.0m;

        if (diffSummary.Count > 0)
        {
            result.DifferingCellSummary = string.Join("; ", diffSummary);
        }

        return result;
    }

    public static bool NormalizeAndAreEqual(string val1, string val2)
    {
        var s1 = val1.Trim();
        var s2 = val2.Trim();

        if (string.Equals(s1, s2, StringComparison.OrdinalIgnoreCase)) return true;

        // Try numeric comparison by value
        if (double.TryParse(s1, NumberStyles.Any, CultureInfo.InvariantCulture, out var d1) &&
            double.TryParse(s2, NumberStyles.Any, CultureInfo.InvariantCulture, out var d2))
        {
            return Math.Abs(d1 - d2) < 0.0001;
        }

        // Try date comparison as instant
        if (DateTime.TryParse(s1, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var dt1) &&
            DateTime.TryParse(s2, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var dt2))
        {
            return dt1 == dt2;
        }

        return false;
    }
}
