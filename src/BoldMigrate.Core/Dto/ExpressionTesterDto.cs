namespace BoldMigrate.Core.Dto;

/// <summary>
/// Request to test, analyze, and evaluate an SSRS expression for Bold Reports compatibility.
/// Inspired by the official Bold Reports AI Expression Builder and Tester tool.
/// </summary>
public class ExpressionTestRequest
{
    /// <summary>The SSRS expression string (e.g. "=IIf(Fields!Sales.Value > 1000, 'High', 'Low')")</summary>
    public string Expression { get; set; } = string.Empty;

    /// <summary>Optional JSON dictionary of mock Fields values (e.g. {"Sales": 1500, "Category": "Hardware"})</summary>
    public string? SampleDataJson { get; set; }

    /// <summary>Optional JSON dictionary of mock Parameters values (e.g. {"StartDate": "2026-01-01"})</summary>
    public string? ParametersJson { get; set; }
}

/// <summary>
/// Analysis and evaluation result for an SSRS expression.
/// </summary>
public class ExpressionTestResult
{
    public string Expression { get; set; } = string.Empty;
    public bool IsValid { get; set; }
    public string CompatibilityStatus { get; set; } = "Compatible"; // Compatible, NeedsReview, Blocked
    public string Category { get; set; } = "Logical";
    public string Explanation { get; set; } = string.Empty;
    public string? EvaluatedValue { get; set; }
    public string? EvaluatedType { get; set; }
    public string? BoldReportsEquivalent { get; set; }
    public string? MigrationAdvice { get; set; }
    public List<string> FunctionsDetected { get; set; } = new();
    public List<string> FieldsDetected { get; set; } = new();
    public List<string> ParametersDetected { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
}
