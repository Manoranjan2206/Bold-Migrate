namespace BoldMigrate.Engine.Validation;

/// <summary>
/// One cell that holds a different value on the two sides, at the same row and column.
/// </summary>
public sealed class DataCellDifference
{
    public int RowNumber { get; set; }
    public string Column { get; set; } = string.Empty;
    public string SourceValue { get; set; } = string.Empty;
    public string TargetValue { get; set; } = string.Empty;
}

/// <summary>
/// A whole record present on only one side.
/// </summary>
public sealed class DataRowDifference
{
    public int RowNumber { get; set; }
    public List<string> Values { get; set; } = [];
}

public enum DataDiffVerdict
{
    /// <summary>Byte-for-byte equivalent data after normalisation: any visual delta is layout only.</summary>
    Identical,

    /// <summary>Same set of records, emitted in a different order (sort or grouping change).</summary>
    ReorderedOnly,

    /// <summary>Same records, but one or more cell values differ.</summary>
    ValuesDiffer,

    /// <summary>Records exist on one side that do not exist on the other.</summary>
    RecordsDiffer,

    /// <summary>The two exports do not share a column layout, so a row diff is not meaningful.</summary>
    SchemaDiffers,

    /// <summary>One or both sides produced no data.</summary>
    NoData
}

public sealed class DataDiffResult
{
    public DataDiffVerdict Verdict { get; set; }
    public string Summary { get; set; } = string.Empty;

    public List<string> SourceColumns { get; set; } = [];
    public List<string> TargetColumns { get; set; } = [];

    public int SourceRowCount { get; set; }
    public int TargetRowCount { get; set; }

    public List<DataCellDifference> CellDifferences { get; set; } = [];
    public List<DataRowDifference> OnlyInSource { get; set; } = [];
    public List<DataRowDifference> OnlyInTarget { get; set; } = [];

    public bool Truncated { get; set; }

    /// <summary>
    /// True when the records match and only their order or rendering position changed. This is the
    /// distinction a pixel diff cannot make, and the reason this engine exists.
    /// </summary>
    public bool IsLayoutOnlyDifference =>
        Verdict is DataDiffVerdict.Identical or DataDiffVerdict.ReorderedOnly;
}

/// <summary>
/// Compares the DATA behind two renders of the same report, rather than their pixels.
///
/// A pixel diff cannot tell "the table moved four pixels" apart from "the query returned different
/// rows" — both show up as red. This engine exports both sides to CSV and answers the question an
/// operator actually has: are these the same records? It reports reordering, changed cell values,
/// and records present on only one side, so a visual delta can be attributed to layout or to data.
/// </summary>
public static class DataDiffEngine
{
    private const int MaxReportedDifferences = 200;

    public static DataDiffResult Compare(string? sourceCsv, string? targetCsv, int maxRows = 10000)
    {
        var result = new DataDiffResult();

        var sourceRows = DataComparer.ParseCsvRows(sourceCsv);
        var targetRows = DataComparer.ParseCsvRows(targetCsv);

        if (sourceRows.Count == 0 || targetRows.Count == 0)
        {
            result.Verdict = DataDiffVerdict.NoData;
            result.Summary = sourceRows.Count == 0 && targetRows.Count == 0
                ? "Neither render produced tabular data to compare."
                : sourceRows.Count == 0
                    ? "The SSRS render produced no tabular data."
                    : "The staged render produced no tabular data.";
            return result;
        }

        result.SourceColumns = sourceRows[0];
        result.TargetColumns = targetRows[0];

        var sourceBody = sourceRows.Skip(1).Take(maxRows).ToList();
        var targetBody = targetRows.Skip(1).Take(maxRows).ToList();
        result.Truncated = sourceRows.Count - 1 > maxRows || targetRows.Count - 1 > maxRows;

        result.SourceRowCount = sourceBody.Count;
        result.TargetRowCount = targetBody.Count;

        if (!ColumnsMatch(result.SourceColumns, result.TargetColumns))
        {
            result.Verdict = DataDiffVerdict.SchemaDiffers;
            result.Summary =
                $"Column layout differs: SSRS returned [{string.Join(", ", result.SourceColumns)}], " +
                $"the staged report returned [{string.Join(", ", result.TargetColumns)}]. " +
                "This is a dataset or field-name change, not a layout shift.";
            return result;
        }

        // Record-level comparison, order-insensitive. Normalising each row the same way
        // DataComparer normalises cells means "1,000.00" and "1000" are one record, not two.
        var sourceKeys = BuildKeyCounts(sourceBody);
        var targetKeys = BuildKeyCounts(targetBody);

        var onlyInSource = SubtractCounts(sourceKeys, targetKeys);
        var onlyInTarget = SubtractCounts(targetKeys, sourceKeys);

        CollectMissingRows(sourceBody, onlyInSource, result.OnlyInSource);
        CollectMissingRows(targetBody, onlyInTarget, result.OnlyInTarget);

        if (result.OnlyInSource.Count > 0 || result.OnlyInTarget.Count > 0)
        {
            // Positional cell differences still help when the row counts line up: they point at
            // the specific columns that drifted rather than just "these records are missing".
            if (sourceBody.Count == targetBody.Count)
            {
                CollectCellDifferences(sourceBody, targetBody, result);
            }

            // Same number of records on both sides, each unmatched row paired with one on the
            // other side: that is a value change within existing records, not added/lost records.
            var sameShape = result.SourceRowCount == result.TargetRowCount
                            && result.OnlyInSource.Count == result.OnlyInTarget.Count
                            && result.CellDifferences.Count > 0;

            if (sameShape)
            {
                result.Verdict = DataDiffVerdict.ValuesDiffer;
                var columns = result.CellDifferences
                    .Select(d => d.Column)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(5)
                    .ToList();
                result.Summary =
                    $"Same {result.SourceRowCount} records, but {result.CellDifferences.Count} cell value(s) differ " +
                    $"in column(s): {string.Join(", ", columns)}. This is a data difference, not a layout shift.";
                return result;
            }

            result.Verdict = DataDiffVerdict.RecordsDiffer;
            var parts = new List<string>();
            if (result.OnlyInSource.Count > 0) parts.Add($"{result.OnlyInSource.Count} record(s) only in SSRS");
            if (result.OnlyInTarget.Count > 0) parts.Add($"{result.OnlyInTarget.Count} record(s) only in the staged render");
            result.Summary =
                $"Data differs: {string.Join(", ", parts)} " +
                $"(SSRS {result.SourceRowCount} rows vs staged {result.TargetRowCount} rows). " +
                "This is a real data difference, not a layout shift.";
            return result;
        }

        // Same multiset of records. Either the order matches exactly, or only the order changed.
        CollectCellDifferences(sourceBody, targetBody, result);

        if (result.CellDifferences.Count == 0)
        {
            result.Verdict = DataDiffVerdict.Identical;
            result.Summary =
                $"Data is identical ({result.SourceRowCount} rows). " +
                "Any visual difference is layout or styling only.";
            return result;
        }

        result.Verdict = DataDiffVerdict.ReorderedOnly;
        result.Summary =
            $"Same {result.SourceRowCount} records on both sides, but they appear in a different order " +
            "(sorting or grouping changed). No records were added or lost.";
        return result;
    }

    private static bool ColumnsMatch(List<string> a, List<string> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
        {
            if (!string.Equals(a[i].Trim(), b[i].Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
        return true;
    }

    private static string RowKey(List<string> row) =>
        string.Join("\u001F", row.Select(NormalizeCell));

    private static string NormalizeCell(string value)
    {
        var trimmed = (value ?? string.Empty).Trim();
        if (trimmed.Length == 0) return string.Empty;

        // Reuse DataComparer's equivalence rules so numeric and date formatting differences between
        // the SSRS and Bold Reports CSV writers are not reported as data changes.
        if (decimal.TryParse(trimmed, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var number))
        {
            return number.ToString("0.##########", System.Globalization.CultureInfo.InvariantCulture);
        }

        if (DateTime.TryParse(trimmed, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var date))
        {
            return date.ToString("yyyy-MM-ddTHH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        }

        return trimmed.ToUpperInvariant();
    }

    private static Dictionary<string, int> BuildKeyCounts(List<List<string>> rows)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var key = RowKey(row);
            counts[key] = counts.TryGetValue(key, out var n) ? n + 1 : 1;
        }
        return counts;
    }

    private static Dictionary<string, int> SubtractCounts(
        Dictionary<string, int> left,
        Dictionary<string, int> right)
    {
        var remaining = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (key, count) in left)
        {
            var other = right.TryGetValue(key, out var n) ? n : 0;
            if (count > other)
            {
                remaining[key] = count - other;
            }
        }
        return remaining;
    }

    private static void CollectMissingRows(
        List<List<string>> rows,
        Dictionary<string, int> missingKeys,
        List<DataRowDifference> into)
    {
        if (missingKeys.Count == 0) return;

        var budget = new Dictionary<string, int>(missingKeys, StringComparer.Ordinal);
        for (var i = 0; i < rows.Count && into.Count < MaxReportedDifferences; i++)
        {
            var key = RowKey(rows[i]);
            if (!budget.TryGetValue(key, out var left) || left <= 0) continue;

            budget[key] = left - 1;
            into.Add(new DataRowDifference { RowNumber = i + 1, Values = rows[i] });
        }
    }

    private static void CollectCellDifferences(
        List<List<string>> sourceBody,
        List<List<string>> targetBody,
        DataDiffResult result)
    {
        var rowCount = Math.Min(sourceBody.Count, targetBody.Count);
        for (var r = 0; r < rowCount && result.CellDifferences.Count < MaxReportedDifferences; r++)
        {
            var sourceRow = sourceBody[r];
            var targetRow = targetBody[r];
            var cols = Math.Min(sourceRow.Count, targetRow.Count);

            for (var c = 0; c < cols && result.CellDifferences.Count < MaxReportedDifferences; c++)
            {
                if (DataComparer.NormalizeAndAreEqual(sourceRow[c], targetRow[c])) continue;

                result.CellDifferences.Add(new DataCellDifference
                {
                    RowNumber = r + 1,
                    Column = c < result.SourceColumns.Count ? result.SourceColumns[c] : $"Column {c + 1}",
                    SourceValue = sourceRow[c],
                    TargetValue = targetRow[c]
                });
            }
        }
    }
}
