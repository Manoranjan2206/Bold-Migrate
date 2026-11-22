using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using BoldMigrate.Core.Dto;

namespace BoldMigrate.Engine.ExpressionTester;

/// <summary>
/// Engine for analyzing, validating, and testing SSRS expressions against Bold Reports standards.
/// Inspired by the official Bold Reports AI SSRS Expression Builder & Tester.
/// (https://www.boldreports.com/free-tools/ai-ssrs-expression-builder-tester/)
/// </summary>
public class ExpressionTesterEngine
{
    private static readonly Regex FieldRegex = new(@"Fields!([a-zA-Z0-9_]+)\.Value|Fields\(""([^""]+)""\)\.Value", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ParamRegex = new(@"Parameters!([a-zA-Z0-9_]+)\.Value|Parameters\(""([^""]+)""\)\.Value", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex FunctionRegex = new(@"\b(IIf|Choose|Switch|Sum|Avg|Count|Min|Max|Lookup|LookupSet|MultiLookup|RunningValue|RowNumber|Previous|Format|FormatCurrency|FormatDateTime|FormatPercent|DateAdd|DateDiff|DatePart|Year|Month|Day|Now|Today|Left|Right|Mid|Len|Trim|UCase|LCase|InStr|Replace|CStr|CInt|CDbl|CDec|CBool|CDate)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CustomAssemblyRegex = new(@"\b(Code\.[a-zA-Z0-9_]+|[a-zA-Z0-9_]+\.[a-zA-Z0-9_]+\.[a-zA-Z0-9_]+)\b", RegexOptions.Compiled);
    private static readonly Regex FrameworkTypeRegex = new(@"\b(System\.[a-zA-Z0-9_\.]+)\b", RegexOptions.Compiled);

    public ExpressionTestResult Evaluate(ExpressionTestRequest request)
    {
        var expr = request.Expression?.Trim() ?? string.Empty;
        var result = new ExpressionTestResult
        {
            Expression = expr,
            IsValid = true
        };

        if (string.IsNullOrWhiteSpace(expr))
        {
            result.IsValid = false;
            result.CompatibilityStatus = "NeedsReview";
            result.Explanation = "Expression is empty. Enter an SSRS expression starting with '='.";
            result.Warnings.Add("Empty expression");
            return result;
        }

        // SSRS expressions typically start with '='
        var cleanExpr = expr.StartsWith("=") ? expr[1..].Trim() : expr;

        // 1. Detect Fields
        foreach (Match m in FieldRegex.Matches(cleanExpr))
        {
            var fieldName = !string.IsNullOrEmpty(m.Groups[1].Value) ? m.Groups[1].Value : m.Groups[2].Value;
            if (!result.FieldsDetected.Contains(fieldName, StringComparer.OrdinalIgnoreCase))
            {
                result.FieldsDetected.Add(fieldName);
            }
        }

        // 2. Detect Parameters
        foreach (Match m in ParamRegex.Matches(cleanExpr))
        {
            var paramName = !string.IsNullOrEmpty(m.Groups[1].Value) ? m.Groups[1].Value : m.Groups[2].Value;
            if (!result.ParametersDetected.Contains(paramName, StringComparer.OrdinalIgnoreCase))
            {
                result.ParametersDetected.Add(paramName);
            }
        }

        // 3. Detect Functions
        foreach (Match m in FunctionRegex.Matches(cleanExpr))
        {
            var fn = m.Groups[1].Value;
            if (!result.FunctionsDetected.Contains(fn, StringComparer.OrdinalIgnoreCase))
            {
                result.FunctionsDetected.Add(fn);
            }
        }

        // 4. Assess Compatibility
        AssessCompatibility(cleanExpr, result);

        // 5. Generate Explanation
        result.Explanation = GenerateExplanation(cleanExpr, result);

        // 6. Safe Evaluation with Mock/Sample Data
        EvaluateMock(cleanExpr, request.SampleDataJson, request.ParametersJson, result);

        return result;
    }

    private static void AssessCompatibility(string expr, ExpressionTestResult result)
    {
        // Custom assembly check
        if (expr.Contains("Code.", StringComparison.OrdinalIgnoreCase) ||
            CustomAssemblyRegex.IsMatch(expr) && !expr.Contains("System.", StringComparison.OrdinalIgnoreCase))
        {
            var match = CustomAssemblyRegex.Match(expr);
            if (match.Success && !match.Value.StartsWith("Fields!", StringComparison.OrdinalIgnoreCase) &&
                !match.Value.StartsWith("Parameters!", StringComparison.OrdinalIgnoreCase) &&
                !match.Value.StartsWith("Globals!", StringComparison.OrdinalIgnoreCase) &&
                !match.Value.StartsWith("User!", StringComparison.OrdinalIgnoreCase))
            {
                result.CompatibilityStatus = "Blocked";
                result.Category = "Custom Assembly / CodeModule";
                result.MigrationAdvice = "Bold Reports operates in a secure web runtime environment where external custom .NET assembly DLLs cannot be dynamically linked without server assembly installation. Extract custom logic into SQL stored procedures or standard inline VB expressions.";
                result.Warnings.Add($"Contains external assembly reference: '{match.Value}'. Custom DLLs cannot be directly executed in web viewer.");
                return;
            }
        }

        // Framework Types
        var sysMatch = FrameworkTypeRegex.Match(expr);
        if (sysMatch.Success)
        {
            result.CompatibilityStatus = "NeedsReview";
            result.Category = "System Framework Method";
            result.MigrationAdvice = "Contains calls to System namespace. Bold Reports supports basic Math and DateTime functions, but verify that the specific System method does not violate client-side sandbox restrictions.";
            result.Warnings.Add($"Uses framework call: '{sysMatch.Value}'. Verify rendering in Bold Reports.");
            return;
        }

        // Scoped Aggregates
        if (result.FunctionsDetected.Any(f => f.Equals("Lookup", StringComparison.OrdinalIgnoreCase) ||
                                              f.Equals("LookupSet", StringComparison.OrdinalIgnoreCase) ||
                                              f.Equals("MultiLookup", StringComparison.OrdinalIgnoreCase) ||
                                              f.Equals("RunningValue", StringComparison.OrdinalIgnoreCase) ||
                                              f.Equals("Previous", StringComparison.OrdinalIgnoreCase)))
        {
            result.CompatibilityStatus = "NeedsReview";
            result.Category = "Scoped Aggregate / Lookup";
            result.MigrationAdvice = "Bold Reports fully supports Lookup and scoped aggregates, provided that both referenced DataSets and Group scopes are correctly mapped during migration.";
            result.Warnings.Add("Scoped lookup function detected: ensure source and destination datasets share identical key schema.");
            return;
        }

        // Fully compatible
        result.CompatibilityStatus = "Compatible";
        result.Category = DetermineCategory(result.FunctionsDetected);
        result.MigrationAdvice = "This expression is 100% compatible with Bold Reports Server and Web Report Viewer. RDL import will translate and execute this expression seamlessly.";
        result.BoldReportsEquivalent = "=" + expr;
    }

    private static string DetermineCategory(List<string> functions)
    {
        if (functions.Any(f => f.Equals("IIf", StringComparison.OrdinalIgnoreCase) || f.Equals("Switch", StringComparison.OrdinalIgnoreCase) || f.Equals("Choose", StringComparison.OrdinalIgnoreCase)))
            return "Conditional / Logical";
        if (functions.Any(f => f.Equals("Sum", StringComparison.OrdinalIgnoreCase) || f.Equals("Avg", StringComparison.OrdinalIgnoreCase) || f.Equals("Count", StringComparison.OrdinalIgnoreCase)))
            return "Aggregate / Statistical";
        if (functions.Any(f => f.Equals("Format", StringComparison.OrdinalIgnoreCase) || f.Equals("FormatCurrency", StringComparison.OrdinalIgnoreCase) || f.Equals("FormatDateTime", StringComparison.OrdinalIgnoreCase)))
            return "Formatting";
        if (functions.Any(f => f.Equals("DateAdd", StringComparison.OrdinalIgnoreCase) || f.Equals("DateDiff", StringComparison.OrdinalIgnoreCase) || f.Equals("Now", StringComparison.OrdinalIgnoreCase)))
            return "Date & Time";
        if (functions.Any(f => f.Equals("Left", StringComparison.OrdinalIgnoreCase) || f.Equals("Right", StringComparison.OrdinalIgnoreCase) || f.Equals("Trim", StringComparison.OrdinalIgnoreCase) || f.Equals("UCase", StringComparison.OrdinalIgnoreCase)))
            return "String Manipulation";

        return "Standard Expression";
    }

    private static string GenerateExplanation(string expr, ExpressionTestResult result)
    {
        var sb = new System.Text.StringBuilder();

        if (expr.StartsWith("IIf(", StringComparison.OrdinalIgnoreCase))
        {
            sb.Append("Evaluates a condition: if true, returns the first branch value; otherwise returns the second branch value.");
        }
        else if (expr.StartsWith("Switch(", StringComparison.OrdinalIgnoreCase))
        {
            sb.Append("Evaluates a sequence of test/value pairs and returns the result corresponding to the first true condition.");
        }
        else if (expr.StartsWith("Sum(", StringComparison.OrdinalIgnoreCase))
        {
            sb.Append("Calculates the arithmetic total of all values for the specified dataset or group scope.");
        }
        else if (expr.StartsWith("Lookup(", StringComparison.OrdinalIgnoreCase))
        {
            sb.Append("Performs a 1-to-1 key lookup against another dataset and retrieves the matching field value.");
        }
        else if (expr.StartsWith("Format(", StringComparison.OrdinalIgnoreCase))
        {
            sb.Append("Formats the value according to the specified numeric or date mask.");
        }
        else
        {
            sb.Append("Computes dynamic report values using standard SSRS expression syntax.");
        }

        if (result.FieldsDetected.Count > 0)
        {
            sb.Append($" Reads dataset field(s): {string.Join(", ", result.FieldsDetected.Select(f => $"'{f}'"))}.");
        }

        if (result.ParametersDetected.Count > 0)
        {
            sb.Append($" Consumes report parameter(s): {string.Join(", ", result.ParametersDetected.Select(p => $"@{p}"))}.");
        }

        return sb.ToString();
    }

    private static void EvaluateMock(string expr, string? sampleDataJson, string? parametersJson, ExpressionTestResult result)
    {
        try
        {
            var fieldMap = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            var paramMap = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

            if (!string.IsNullOrWhiteSpace(sampleDataJson))
            {
                try
                {
                    using var doc = JsonDocument.Parse(sampleDataJson);
                    foreach (var prop in doc.RootElement.EnumerateObject())
                    {
                        fieldMap[prop.Name] = prop.Value.ValueKind switch
                        {
                            JsonValueKind.Number => prop.Value.GetDouble(),
                            JsonValueKind.True => true,
                            JsonValueKind.False => false,
                            _ => prop.Value.GetString()
                        };
                    }
                }
                catch { }
            }

            if (!string.IsNullOrWhiteSpace(parametersJson))
            {
                try
                {
                    using var doc = JsonDocument.Parse(parametersJson);
                    foreach (var prop in doc.RootElement.EnumerateObject())
                    {
                        paramMap[prop.Name] = prop.Value.ValueKind switch
                        {
                            JsonValueKind.Number => prop.Value.GetDouble(),
                            JsonValueKind.True => true,
                            JsonValueKind.False => false,
                            _ => prop.Value.GetString()
                        };
                    }
                }
                catch { }
            }

            // Fill default mock values if not provided
            foreach (var f in result.FieldsDetected)
            {
                if (!fieldMap.ContainsKey(f)) fieldMap[f] = 1250.50;
            }
            foreach (var p in result.ParametersDetected)
            {
                if (!paramMap.ContainsKey(p)) paramMap[p] = "All";
            }

            // Simple evaluator for common IIf patterns: IIf(condition, trueVal, falseVal)
            var iifMatch = Regex.Match(expr, @"^IIf\s*\((.*)\)$", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (iifMatch.Success)
            {
                var args = SplitTopLevelArguments(iifMatch.Groups[1].Value);
                if (args.Count >= 3)
                {
                    var condStr = args[0].Trim();
                    var trueValStr = args[1].Trim().Trim('"', '\'');
                    var falseValStr = args[2].Trim().Trim('"', '\'');

                    var condPassed = EvaluateCondition(condStr, fieldMap, paramMap);
                    result.EvaluatedValue = condPassed ? trueValStr : falseValStr;
                    result.EvaluatedType = "String";
                    return;
                }
            }

            // Format evaluator: Format(val, "format")
            var fmtMatch = Regex.Match(expr, @"^Format\s*\((.*)\)$", RegexOptions.IgnoreCase);
            if (fmtMatch.Success)
            {
                var args = SplitTopLevelArguments(fmtMatch.Groups[1].Value);
                if (args.Count >= 2)
                {
                    var valStr = args[0].Trim();
                    var mask = args[1].Trim().Trim('"', '\'');
                    var resolvedVal = ResolveValue(valStr, fieldMap, paramMap);
                    if (resolvedVal is double d)
                    {
                        if (mask.StartsWith("$") && (mask.Contains("0.00") || mask.Contains("#,##0")))
                        {
                            result.EvaluatedValue = "$" + d.ToString("N2", CultureInfo.InvariantCulture);
                        }
                        else
                        {
                            try
                            {
                                result.EvaluatedValue = d.ToString(mask, CultureInfo.InvariantCulture);
                            }
                            catch
                            {
                                result.EvaluatedValue = d.ToString("N2", CultureInfo.InvariantCulture);
                            }
                        }
                    }
                    else if (resolvedVal is DateTime dt)
                    {
                        result.EvaluatedValue = dt.ToString(mask, CultureInfo.InvariantCulture);
                    }
                    else
                    {
                        result.EvaluatedValue = resolvedVal?.ToString() ?? "";
                    }
                    result.EvaluatedType = "FormattedText";
                    return;
                }
            }

            // Sum evaluator: Sum(Fields!Name.Value)
            var sumMatch = Regex.Match(expr, @"^Sum\s*\((.*)\)$", RegexOptions.IgnoreCase);
            if (sumMatch.Success)
            {
                var arg = sumMatch.Groups[1].Value.Trim();
                var val = ResolveValue(arg, fieldMap, paramMap);
                var num = val is double d ? d : 1250.50;
                result.EvaluatedValue = (num * 3).ToString("N2", CultureInfo.InvariantCulture) + " (Simulated 3 Rows)";
                result.EvaluatedType = "Numeric Total";
                return;
            }

            // UCase / LCase
            var caseMatch = Regex.Match(expr, @"^(UCase|LCase)\s*\((.*)\)$", RegexOptions.IgnoreCase);
            if (caseMatch.Success)
            {
                var fn = caseMatch.Groups[1].Value.ToUpperInvariant();
                var val = ResolveValue(caseMatch.Groups[2].Value.Trim(), fieldMap, paramMap)?.ToString() ?? "";
                result.EvaluatedValue = fn == "UCASE" ? val.ToUpperInvariant() : val.ToLowerInvariant();
                result.EvaluatedType = "String";
                return;
            }

            // Default fallback
            result.EvaluatedValue = "Mock evaluation successful: Valid expression";
            result.EvaluatedType = "Variant";
        }
        catch (Exception ex)
        {
            result.EvaluatedValue = $"Evaluation simulated: {ex.Message}";
            result.EvaluatedType = "Error";
        }
    }

    private static object? ResolveValue(string token, Dictionary<string, object?> fields, Dictionary<string, object?> parameters)
    {
        var fMatch = FieldRegex.Match(token);
        if (fMatch.Success)
        {
            var fn = !string.IsNullOrEmpty(fMatch.Groups[1].Value) ? fMatch.Groups[1].Value : fMatch.Groups[2].Value;
            if (fields.TryGetValue(fn, out var v)) return v;
        }

        var pMatch = ParamRegex.Match(token);
        if (pMatch.Success)
        {
            var pn = !string.IsNullOrEmpty(pMatch.Groups[1].Value) ? pMatch.Groups[1].Value : pMatch.Groups[2].Value;
            if (parameters.TryGetValue(pn, out var v)) return v;
        }

        if (double.TryParse(token, NumberStyles.Any, CultureInfo.InvariantCulture, out var num))
            return num;

        return token.Trim('"', '\'');
    }

    private static bool EvaluateCondition(string cond, Dictionary<string, object?> fields, Dictionary<string, object?> parameters)
    {
        // Check for comparison: >=, <=, >, <, =, <>
        var comp = Regex.Match(cond, @"(.*?)(>=|<=|<>|>|<|=)(.*)");
        if (comp.Success)
        {
            var left = ResolveValue(comp.Groups[1].Value.Trim(), fields, parameters);
            var op = comp.Groups[2].Value;
            var right = ResolveValue(comp.Groups[3].Value.Trim(), fields, parameters);

            if (left is double lNum && right is double rNum)
            {
                return op switch
                {
                    ">" => lNum > rNum,
                    ">=" => lNum >= rNum,
                    "<" => lNum < rNum,
                    "<=" => lNum <= rNum,
                    "=" => Math.Abs(lNum - rNum) < 0.0001,
                    "<>" => Math.Abs(lNum - rNum) >= 0.0001,
                    _ => false
                };
            }

            var lStr = left?.ToString() ?? "";
            var rStr = right?.ToString() ?? "";
            return op switch
            {
                "=" => string.Equals(lStr, rStr, StringComparison.OrdinalIgnoreCase),
                "<>" => !string.Equals(lStr, rStr, StringComparison.OrdinalIgnoreCase),
                _ => true
            };
        }

        return true;
    }

    private static List<string> SplitTopLevelArguments(string input)
    {
        var list = new List<string>();
        var depth = 0;
        var start = 0;
        var inDoubleQuotes = false;
        var inSingleQuotes = false;

        for (int i = 0; i < input.Length; i++)
        {
            var c = input[i];
            if (c == '"' && !inSingleQuotes) inDoubleQuotes = !inDoubleQuotes;
            else if (c == '\'' && !inDoubleQuotes) inSingleQuotes = !inSingleQuotes;
            else if (!inDoubleQuotes && !inSingleQuotes)
            {
                if (c == '(') depth++;
                else if (c == ')') depth--;
                else if (c == ',' && depth == 0)
                {
                    list.Add(input[start..i]);
                    start = i + 1;
                }
            }
        }

        if (start < input.Length)
        {
            list.Add(input[start..]);
        }

        return list;
    }

    public static List<ExpressionTestRequest> GetSampleExpressions()
    {
        return new List<ExpressionTestRequest>
        {
            new()
            {
                Expression = "=IIf(Fields!Sales.Value > 1000, \"High Performer\", \"Standard\")",
                SampleDataJson = "{\"Sales\": 1450.00, \"Region\": \"North America\"}",
                ParametersJson = "{\"Threshold\": 1000}"
            },
            new()
            {
                Expression = "=Format(Fields!Revenue.Value, \"$#,##0.00\")",
                SampleDataJson = "{\"Revenue\": 125890.75}",
                ParametersJson = "{}"
            },
            new()
            {
                Expression = "=Lookup(Fields!ProductID.Value, Fields!ID.Value, Fields!ProductName.Value, \"ProductCatalog\")",
                SampleDataJson = "{\"ProductID\": 402}",
                ParametersJson = "{}"
            },
            new()
            {
                Expression = "=Sum(Fields!OrderTotal.Value, \"CategoryGroup\")",
                SampleDataJson = "{\"OrderTotal\": 230.00}",
                ParametersJson = "{}"
            },
            new()
            {
                Expression = "=Switch(Fields!Score.Value >= 90, \"Grade A\", Fields!Score.Value >= 80, \"Grade B\", True, \"Needs Improvement\")",
                SampleDataJson = "{\"Score\": 88}",
                ParametersJson = "{}"
            },
            new()
            {
                Expression = "=MyCustomLibrary.TaxCalculator.ComputeRate(Fields!SubTotal.Value)",
                SampleDataJson = "{\"SubTotal\": 500}",
                ParametersJson = "{}"
            }
        };
    }
}
