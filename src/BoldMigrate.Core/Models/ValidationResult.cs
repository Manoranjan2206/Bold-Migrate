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

    /// <summary>
    /// Similarity of the PDF renders from both servers, or null when a PDF render could not be
    /// obtained from one of them. Null means "not measured" — it must never be treated as 0 or 100.
    /// </summary>
    [JsonPropertyName("visualSimilarity")]
    public decimal? VisualSimilarity { get; set; }

    [JsonPropertyName("rowsSource")]
    public long RowsSource { get; set; }

    [JsonPropertyName("rowsDestination")]
    public long RowsDestination { get; set; }

    [JsonPropertyName("layoutShifts")]
    public int LayoutShifts { get; set; }

    /// <summary>Pages in the SSRS PDF render; null when no PDF render was obtained.</summary>
    [JsonPropertyName("sourcePageCount")]
    public int? SourcePageCount { get; set; }

    /// <summary>Pages in the Bold Reports PDF render; null when no PDF render was obtained.</summary>
    [JsonPropertyName("destinationPageCount")]
    public int? DestinationPageCount { get; set; }

    /// <summary>Human-readable outcome of the render comparison.</summary>
    [JsonPropertyName("renderSummary")]
    public string? RenderSummary { get; set; }

    [JsonPropertyName("renderStatus")]
    public string? RenderStatus { get; set; }

    [JsonPropertyName("renderDurationMs")]
    public long? RenderDurationMs { get; set; }

    [JsonPropertyName("screenshotRelativePath")]
    public string? ScreenshotRelativePath { get; set; }

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; set; }

    [JsonPropertyName("parameterSetHash")]
    public string ParameterSetHash { get; set; } = string.Empty;

    [JsonPropertyName("runUtc")]
    public DateTime RunUtc { get; set; } = DateTime.UtcNow;
}
