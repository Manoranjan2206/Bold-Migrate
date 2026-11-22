using System.Text.Json.Serialization;

namespace BoldMigrate.Core.Models;

/// <summary>
/// Records an individual upload attempt for a report against the destination server.
/// </summary>
public class UploadAttempt
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [JsonPropertyName("reportId")]
    public Guid ReportId { get; set; }

    [JsonPropertyName("attemptNumber")]
    public int AttemptNumber { get; set; }

    [JsonPropertyName("httpStatus")]
    public int HttpStatus { get; set; }

    [JsonPropertyName("durationMs")]
    public long DurationMs { get; set; }

    [JsonPropertyName("errorCode")]
    public string? ErrorCode { get; set; }
}
