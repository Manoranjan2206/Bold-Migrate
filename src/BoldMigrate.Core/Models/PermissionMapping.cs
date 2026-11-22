using System.Text.Json.Serialization;

namespace BoldMigrate.Core.Models;

/// <summary>
/// Maps an SSRS security role assignment to a Bold Reports permission or role.
/// </summary>
public class PermissionMapping
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [JsonPropertyName("runId")]
    public Guid RunId { get; set; }

    [JsonPropertyName("sourcePath")]
    public string SourcePath { get; set; } = string.Empty;

    [JsonPropertyName("principal")]
    public string Principal { get; set; } = string.Empty;

    [JsonPropertyName("sourceRole")]
    public string SourceRole { get; set; } = string.Empty;

    [JsonPropertyName("destinationRole")]
    public string DestinationRole { get; set; } = string.Empty;

    [JsonPropertyName("mappingStatus")]
    public string MappingStatus { get; set; } = string.Empty;
}
