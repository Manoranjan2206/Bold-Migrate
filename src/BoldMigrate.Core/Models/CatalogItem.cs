using System.Text.Json.Serialization;
using BoldMigrate.Core.Enums;

namespace BoldMigrate.Core.Models;

/// <summary>
/// Represents one object discovered on the source server before any judgement is made about it.
/// </summary>
public class CatalogItem
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [JsonPropertyName("runId")]
    public Guid RunId { get; set; }

    [JsonPropertyName("itemType")]
    public ItemType ItemType { get; set; }

    [JsonPropertyName("sourcePath")]
    public string SourcePath { get; set; } = string.Empty;

    [JsonPropertyName("sourceItemId")]
    public string SourceItemId { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("parentPath")]
    public string ParentPath { get; set; } = string.Empty;

    [JsonPropertyName("sizeBytes")]
    public long SizeBytes { get; set; }

    [JsonPropertyName("modifiedUtc")]
    public DateTime ModifiedUtc { get; set; }

    [JsonPropertyName("stagedFilePath")]
    public string? StagedFilePath { get; set; }

    [JsonPropertyName("hash")]
    public string? Hash { get; set; }

    [JsonPropertyName("destinationItemId")]
    public string? DestinationItemId { get; set; }

    [JsonIgnore]
    public MigrationRun? MigrationRun { get; set; }

    [JsonIgnore]
    public ReportRecord? ReportRecord { get; set; }
}
