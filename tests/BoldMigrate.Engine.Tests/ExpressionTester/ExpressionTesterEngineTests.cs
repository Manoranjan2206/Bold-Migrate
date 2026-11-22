using BoldMigrate.Core.Dto;
using BoldMigrate.Engine.ExpressionTester;
using Xunit;

namespace BoldMigrate.Engine.Tests.ExpressionTester;

public class ExpressionTesterEngineTests
{
    private readonly ExpressionTesterEngine _engine = new();

    [Fact]
    public void Evaluate_SimpleIIf_ReturnsCompatibleAndEvaluatesTrueBranch()
    {
        var req = new ExpressionTestRequest
        {
            Expression = "=IIf(Fields!Sales.Value > 1000, \"Bonus Eligible\", \"Standard\")",
            SampleDataJson = "{\"Sales\": 1500}",
            ParametersJson = "{}"
        };

        var result = _engine.Evaluate(req);

        Assert.True(result.IsValid);
        Assert.Equal("Compatible", result.CompatibilityStatus);
        Assert.Equal("Bonus Eligible", result.EvaluatedValue);
        Assert.Contains("IIf", result.FunctionsDetected);
        Assert.Contains("Sales", result.FieldsDetected);
        Assert.NotEmpty(result.Explanation);
    }

    [Fact]
    public void Evaluate_IIfFalseBranch_ReturnsStandard()
    {
        var req = new ExpressionTestRequest
        {
            Expression = "=IIf(Fields!Sales.Value > 1000, \"Bonus Eligible\", \"Standard\")",
            SampleDataJson = "{\"Sales\": 500}",
            ParametersJson = "{}"
        };

        var result = _engine.Evaluate(req);

        Assert.True(result.IsValid);
        Assert.Equal("Compatible", result.CompatibilityStatus);
        Assert.Equal("Standard", result.EvaluatedValue);
    }

    [Fact]
    public void Evaluate_FormatExpression_FormatsNumberCorrectly()
    {
        var req = new ExpressionTestRequest
        {
            Expression = "=Format(Fields!Total.Value, \"$#,##0.00\")",
            SampleDataJson = "{\"Total\": 12500.5}",
            ParametersJson = "{}"
        };

        var result = _engine.Evaluate(req);

        Assert.True(result.IsValid);
        Assert.Equal("Compatible", result.CompatibilityStatus);
        Assert.Equal("$12,500.50", result.EvaluatedValue);
        Assert.Contains("Format", result.FunctionsDetected);
    }

    [Fact]
    public void Evaluate_CustomAssembly_FlagsAsBlocked()
    {
        var req = new ExpressionTestRequest
        {
            Expression = "=MyCompany.BillingUtils.CalculateTax(Fields!SubTotal.Value)",
            SampleDataJson = "{\"SubTotal\": 200}"
        };

        var result = _engine.Evaluate(req);

        Assert.Equal("Blocked", result.CompatibilityStatus);
        Assert.NotEmpty(result.Warnings);
        Assert.Contains("Custom DLLs cannot be directly executed in web viewer", result.Warnings[0]);
    }

    [Fact]
    public void Evaluate_LookupFunction_FlagsAsNeedsReviewForScopeValidation()
    {
        var req = new ExpressionTestRequest
        {
            Expression = "=Lookup(Fields!CustId.Value, Fields!ID.Value, Fields!Name.Value, \"CustomerDetails\")"
        };

        var result = _engine.Evaluate(req);

        Assert.Equal("NeedsReview", result.CompatibilityStatus);
        Assert.Contains("Lookup", result.FunctionsDetected);
        Assert.Contains("CustId", result.FieldsDetected);
    }

    [Fact]
    public void Evaluate_FrameworkType_FlagsAsNeedsReview()
    {
        var req = new ExpressionTestRequest
        {
            Expression = "=System.Math.Round(Fields!Amount.Value, 2)"
        };

        var result = _engine.Evaluate(req);

        Assert.Equal("NeedsReview", result.CompatibilityStatus);
        Assert.Contains("System.Math", result.Warnings[0]);
    }
}
