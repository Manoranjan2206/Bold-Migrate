using System.Text.Json.Serialization;

namespace BoldMigrate.Core.Models;

/// <summary>
/// Maps a legacy SSRS report URL to the corresponding destination URL in Bold Reports.
/// </summary>
public class UrlMapping
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [JsonPropertyName("runId")]
    public Guid RunId { get; set; }

    [JsonPropertyName("sourcePath")]
    public string SourcePath { get; set; } = string.Empty;

    [JsonPropertyName("destinationItemId")]
    public string DestinationItemId { get; set; } = string.Empty;

    [JsonPropertyName("destinationUrl")]
    public string DestinationUrl { get; set; } = string.Empty;
}
