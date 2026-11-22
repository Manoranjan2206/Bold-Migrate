using System.Text.Json.Serialization;

namespace BoldMigrate.Core.Models;

/// <summary>
/// Represents a change made by the transform engine to an RDL definition.
/// </summary>
public class TransformAction
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [JsonPropertyName("reportId")]
    public Guid ReportId { get; set; }

    [JsonPropertyName("actionCode")]
    public string ActionCode { get; set; } = string.Empty;

    [JsonPropertyName("before")]
    public string? Before { get; set; }

    [JsonPropertyName("after")]
    public string? After { get; set; }

    [JsonPropertyName("attemptNumber")]
    public int AttemptNumber { get; set; }
}
