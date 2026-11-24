using System.Text.Json.Serialization;

namespace BoldMigrate.Core.Models;

/// <summary>
/// Represents an error or diagnostic log captured during local report rendering via IReportLogger.
/// </summary>
public class RenderLogEntry
{
    [JsonPropertyName("timestampUtc")]
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("exceptionMessage")]
    public string? ExceptionMessage { get; set; }

    [JsonPropertyName("stackTrace")]
    public string? StackTrace { get; set; }

    [JsonPropertyName("methodName")]
    public string? MethodName { get; set; }

    [JsonPropertyName("errorType")]
    public string ErrorType { get; set; } = string.Empty;
}
