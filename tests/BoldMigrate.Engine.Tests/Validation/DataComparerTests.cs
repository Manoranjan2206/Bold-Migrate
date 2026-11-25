using BoldMigrate.Engine.Validation;
using Xunit;

namespace BoldMigrate.Engine.Tests.Validation;

public class DataComparerTests
{
    [Fact]
    public void CompareCsvData_IdenticalContent_Scores100Percent()
    {
        var csv = "Id,Name,Amount,Date\r\n1,Alpha,100.50,2026-11-21\r\n2,Beta,250.00,2026-11-22";

        var result = DataComparer.CompareCsvData(csv, csv);

        Assert.Equal(100.0m, result.AccuracyPercentage);
        Assert.True(result.RowCountMatched);
        Assert.Equal(0, result.DifferingCellsCount);
    }

    [Fact]
    public void CompareCsvData_DifferentRowCounts_ScoresZeroPercent()
    {
        var src = "Id,Name\r\n1,A\r\n2,B";
        var dst = "Id,Name\r\n1,A";

        var result = DataComparer.CompareCsvData(src, dst);

        Assert.Equal(0.0m, result.AccuracyPercentage);
        Assert.False(result.RowCountMatched);
        Assert.Contains("Row count mismatch", result.DifferingCellSummary);
    }

    [Fact]
    public void CompareCsvData_NumericAndDateEquivalence_MatchesCorrectly()
    {
        var src = "ColNum,ColDate,ColText\r\n100.0,2026-11-21T00:00:00Z,Hello \r\n";
        var dst = "ColNum,ColDate,ColText\r\n100,2026-11-21,Hello\r\n";

        var result = DataComparer.CompareCsvData(src, dst);

        Assert.Equal(100.0m, result.AccuracyPercentage);
        Assert.Equal(0, result.DifferingCellsCount);
    }

    [Fact]
    public void CompareCsvData_OneCellDiffering_CatchesMismatch()
    {
        var src = "Id,Amount\r\n1,100\r\n2,200\r\n";
        var dst = "Id,Amount\r\n1,100\r\n2,999\r\n";

        var result = DataComparer.CompareCsvData(src, dst);

        Assert.True(result.AccuracyPercentage < 100.0m);
        Assert.Equal(1, result.DifferingCellsCount);
        Assert.Contains("Source='200' vs Dest='999'", result.DifferingCellSummary);
    }

    [Fact]
    public void CompareCsvData_QuotedCommasEscapedQuotesAndBom_ParsesRfc4180Correctly()
    {
        var src = "\uFEFFId,Company,Note\r\n1,\"Acme, Corp\",\"Quoted \"\"VIP\"\" account\"\r\n2,\"Line1\nLine2\",Simple\r\n";
        var dst = "Id,Company,Note\n1,\"Acme, Corp\",\"Quoted \"\"VIP\"\" account\"\n2,\"Line1\nLine2\",Simple\n";

        var result = DataComparer.CompareCsvData(src, dst);

        Assert.Equal(100.0m, result.AccuracyPercentage);
        Assert.True(result.RowCountMatched);
        Assert.Equal(0, result.DifferingCellsCount);
    }
}
