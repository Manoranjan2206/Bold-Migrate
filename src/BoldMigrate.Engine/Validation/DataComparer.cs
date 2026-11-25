using System.Globalization;
using System.Text;

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

        var sourceRows = ParseCsvRows(sourceCsv);
        var destRows = ParseCsvRows(destinationCsv);

        result.RowsSource = Math.Max(0, sourceRows.Count - 1);
        result.RowsDestination = Math.Max(0, destRows.Count - 1);

        if (sourceRows.Count == 0 && destRows.Count == 0)
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

        var rowsToCompare = Math.Min(sourceRows.Count, sampleRowLimit);
        if (sourceRows.Count > sampleRowLimit)
        {
            result.UsedSampling = true;
        }

        for (var i = 0; i < rowsToCompare; i++)
        {
            var sCells = sourceRows[i];
            var dCells = destRows[i];

            var maxCols = Math.Max(sCells.Count, dCells.Count);
            for (var c = 0; c < maxCols; c++)
            {
                totalCellsCompared++;
                var sVal = c < sCells.Count ? sCells[c] : "";
                var dVal = c < dCells.Count ? dCells[c] : "";

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

    /// <summary>
    /// RFC-4180 aware CSV parser supporting quoted fields with commas, doubled quotes (""),
    /// embedded newlines inside quoted fields, and leading UTF-8 BOM stripping.
    /// </summary>
    public static List<List<string>> ParseCsvRows(string? csv)
    {
        var rows = new List<List<string>>();
        if (string.IsNullOrEmpty(csv)) return rows;

        var text = csv[0] == '\uFEFF' ? csv[1..] : csv;
        var currentRow = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];

            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    field.Append(ch);
                }
                continue;
            }

            if (ch == '"' && field.Length == 0)
            {
                inQuotes = true;
            }
            else if (ch == ',')
            {
                currentRow.Add(field.ToString());
                field.Clear();
            }
            else if (ch == '\r' || ch == '\n')
            {
                if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }
                currentRow.Add(field.ToString());
                field.Clear();
                if (currentRow.Count > 1 || (currentRow.Count == 1 && currentRow[0].Length > 0))
                {
                    rows.Add(currentRow);
                }
                currentRow = new List<string>();
            }
            else
            {
                field.Append(ch);
            }
        }

        if (field.Length > 0 || currentRow.Count > 0)
        {
            currentRow.Add(field.ToString());
            if (currentRow.Count > 1 || (currentRow.Count == 1 && currentRow[0].Length > 0))
            {
                rows.Add(currentRow);
            }
        }

        return rows;
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
