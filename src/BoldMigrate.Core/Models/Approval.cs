using System.Text.Json.Serialization;

namespace BoldMigrate.Core.Models;

/// <summary>
/// Records human approval or rejection of a report validation result.
/// </summary>
public class Approval
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [JsonPropertyName("reportId")]
    public Guid ReportId { get; set; }

    [JsonPropertyName("decision")]
    public string Decision { get; set; } = string.Empty;

    [JsonPropertyName("userId")]
    public string UserId { get; set; } = string.Empty;

    [JsonPropertyName("userName")]
    public string UserName { get; set; } = string.Empty;

    [JsonPropertyName("decidedUtc")]
    public DateTime DecidedUtc { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("comment")]
    public string? Comment { get; set; }
}
