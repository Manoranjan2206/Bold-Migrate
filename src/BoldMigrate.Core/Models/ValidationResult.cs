using System.Text.Json.Serialization;

namespace BoldMigrate.Core.Models;

/// <summary>
/// Holds output comparison scores and metadata for one report validation run.
/// </summary>
public class ValidationResult
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [JsonPropertyName("reportId")]
    public Guid ReportId { get; set; }

    [JsonPropertyName("dataAccuracy")]
    public decimal DataAccuracy { get; set; }

    [JsonPropertyName("visualSimilarity")]
    public decimal VisualSimilarity { get; set; }

    [JsonPropertyName("rowsSource")]
    public long RowsSource { get; set; }

    [JsonPropertyName("rowsDestination")]
    public long RowsDestination { get; set; }

    [JsonPropertyName("layoutShifts")]
    public int LayoutShifts { get; set; }

    [JsonPropertyName("parameterSetHash")]
    public string ParameterSetHash { get; set; } = string.Empty;

    [JsonPropertyName("runUtc")]
    public DateTime RunUtc { get; set; } = DateTime.UtcNow;
}
