using BoldMigrate.Engine.Validation;
using Xunit;

namespace BoldMigrate.Engine.Tests.Validation;

/// <summary>
/// The point of this engine is to answer the one question a pixel diff cannot: is a visual
/// difference caused by layout, or by the data? Each test pins one of those answers.
/// </summary>
public class DataDiffEngineTests
{
    private const string Baseline =
        "Region,Product,Amount\n" +
        "North,Widget,100\n" +
        "South,Gadget,250\n" +
        "East,Doohickey,75\n";

    [Fact]
    public void Compare_IdenticalData_ReportsLayoutOnly()
    {
        var result = DataDiffEngine.Compare(Baseline, Baseline);

        Assert.Equal(DataDiffVerdict.Identical, result.Verdict);
        Assert.True(result.IsLayoutOnlyDifference);
        Assert.Empty(result.CellDifferences);
        Assert.Empty(result.OnlyInSource);
        Assert.Empty(result.OnlyInTarget);
        Assert.Contains("layout", result.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compare_NumericAndDateFormattingDifferences_AreNotDataChanges()
    {
        // SSRS and Bold Reports CSV writers format numbers and dates differently. That is a
        // rendering difference, not a data difference, and must not be reported as one.
        const string source = "Product,Amount,Sold\nWidget,\"1,000.00\",2026-01-05\n";
        const string target = "Product,Amount,Sold\nWidget,1000,01/05/2026\n";

        var result = DataDiffEngine.Compare(source, target);

        Assert.Equal(DataDiffVerdict.Identical, result.Verdict);
        Assert.True(result.IsLayoutOnlyDifference);
    }

    [Fact]
    public void Compare_SameRecordsDifferentOrder_IsReorderedNotDataLoss()
    {
        const string reordered =
            "Region,Product,Amount\n" +
            "South,Gadget,250\n" +
            "East,Doohickey,75\n" +
            "North,Widget,100\n";

        var result = DataDiffEngine.Compare(Baseline, reordered);

        Assert.Equal(DataDiffVerdict.ReorderedOnly, result.Verdict);
        Assert.True(result.IsLayoutOnlyDifference);
        Assert.Empty(result.OnlyInSource);
        Assert.Empty(result.OnlyInTarget);
        Assert.Equal(3, result.SourceRowCount);
        Assert.Equal(3, result.TargetRowCount);
    }

    [Fact]
    public void Compare_ChangedCellValue_IsReportedWithColumnAndBothValues()
    {
        const string changed =
            "Region,Product,Amount\n" +
            "North,Widget,100\n" +
            "South,Gadget,999\n" +
            "East,Doohickey,75\n";

        var result = DataDiffEngine.Compare(Baseline, changed);

        Assert.Equal(DataDiffVerdict.ValuesDiffer, result.Verdict);
        Assert.False(result.IsLayoutOnlyDifference);

        var diff = Assert.Single(result.CellDifferences);
        Assert.Equal("Amount", diff.Column);
        Assert.Equal(2, diff.RowNumber);
        Assert.Equal("250", diff.SourceValue);
        Assert.Equal("999", diff.TargetValue);
    }

    [Fact]
    public void Compare_MissingRecord_IsReportedAsRecordsDiffer()
    {
        const string missingRow =
            "Region,Product,Amount\n" +
            "North,Widget,100\n" +
            "East,Doohickey,75\n";

        var result = DataDiffEngine.Compare(Baseline, missingRow);

        Assert.Equal(DataDiffVerdict.RecordsDiffer, result.Verdict);
        Assert.False(result.IsLayoutOnlyDifference);
        Assert.Equal(3, result.SourceRowCount);
        Assert.Equal(2, result.TargetRowCount);

        var missing = Assert.Single(result.OnlyInSource);
        Assert.Contains("Gadget", missing.Values);
        Assert.Empty(result.OnlyInTarget);
    }

    [Fact]
    public void Compare_ExtraRecord_IsAttributedToTheStagedRender()
    {
        const string extraRow = Baseline + "West,Thing,10\n";

        var result = DataDiffEngine.Compare(Baseline, extraRow);

        Assert.Equal(DataDiffVerdict.RecordsDiffer, result.Verdict);
        var extra = Assert.Single(result.OnlyInTarget);
        Assert.Contains("Thing", extra.Values);
        Assert.Empty(result.OnlyInSource);
    }

    [Fact]
    public void Compare_DifferentColumns_IsSchemaChangeNotRowDiff()
    {
        const string differentSchema = "Region,Product,Total\nNorth,Widget,100\n";

        var result = DataDiffEngine.Compare(Baseline, differentSchema);

        Assert.Equal(DataDiffVerdict.SchemaDiffers, result.Verdict);
        Assert.False(result.IsLayoutOnlyDifference);
        Assert.Contains("Column layout differs", result.Summary);
    }

    [Theory]
    [InlineData("", "Region\nNorth\n")]
    [InlineData("Region\nNorth\n", "")]
    [InlineData(null, null)]
    public void Compare_EmptyRenders_ReportNoData(string? source, string? target)
    {
        var result = DataDiffEngine.Compare(source, target);

        Assert.Equal(DataDiffVerdict.NoData, result.Verdict);
        Assert.False(result.IsLayoutOnlyDifference);
    }

    [Fact]
    public void Compare_QuotedCommasInValues_AreParsedAsSingleCells()
    {
        const string source = "Customer,Note\n\"Acme, Inc.\",\"Paid, in full\"\n";
        const string target = "Customer,Note\n\"Acme, Inc.\",\"Paid, in part\"\n";

        var result = DataDiffEngine.Compare(source, target);

        Assert.Equal(DataDiffVerdict.ValuesDiffer, result.Verdict);
        var diff = Assert.Single(result.CellDifferences);
        Assert.Equal("Note", diff.Column);
        Assert.Equal("Paid, in full", diff.SourceValue);
    }
}
