using System.Text.Json.Serialization;
using BoldMigrate.Core.Enums;

namespace BoldMigrate.Core.Dto;

/// <summary>
/// Data transfer representation of a catalog item discovered on the source server.
/// </summary>
public class CatalogItemDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("parentPath")]
    public string? ParentPath { get; set; }

    [JsonPropertyName("itemType")]
    public ItemType ItemType { get; set; }

    [JsonPropertyName("sizeBytes")]
    public long SizeBytes { get; set; }

    [JsonPropertyName("modifiedUtc")]
    public DateTime ModifiedUtc { get; set; }
}
