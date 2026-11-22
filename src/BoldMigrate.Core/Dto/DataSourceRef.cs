using System.Text.Json.Serialization;

namespace BoldMigrate.Core.Dto;

/// <summary>
/// Details of a data source referenced by or embedded within a report.
/// </summary>
public class DataSourceRef
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("referencePath")]
    public string? ReferencePath { get; set; }

    [JsonPropertyName("reboundId")]
    public string? ReboundId { get; set; }

    [JsonPropertyName("isEmbedded")]
    public bool IsEmbedded { get; set; }

    [JsonPropertyName("connectionString")]
    public string? ConnectionString { get; set; }

    [JsonPropertyName("extension")]
    public string? Extension { get; set; }
}
