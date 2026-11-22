using System.Text.Json.Serialization;

namespace BoldMigrate.Core.Models;

/// <summary>
/// Records an administrative or user action taken within a migration run for auditing.
/// </summary>
public class AuditEvent
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [JsonPropertyName("runId")]
    public Guid RunId { get; set; }

    [JsonPropertyName("userId")]
    public string UserId { get; set; } = string.Empty;

    [JsonPropertyName("action")]
    public string Action { get; set; } = string.Empty;

    [JsonPropertyName("targetType")]
    public string TargetType { get; set; } = string.Empty;

    [JsonPropertyName("targetId")]
    public string TargetId { get; set; } = string.Empty;

    [JsonPropertyName("occurredUtc")]
    public DateTime OccurredUtc { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("detail")]
    public string? Detail { get; set; }
}
