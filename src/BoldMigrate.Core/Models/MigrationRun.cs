using System.Text.Json.Serialization;
using BoldMigrate.Core.Enums;

namespace BoldMigrate.Core.Models;

/// <summary>
/// Represents one end-to-end migration run against a source and destination server pair.
/// </summary>
public class MigrationRun
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public RunStatus Status { get; set; } = RunStatus.Draft;

    [JsonPropertyName("currentStep")]
    public int CurrentStep { get; set; } = 1;

    [JsonPropertyName("sourceServerUrl")]
    public string SourceServerUrl { get; set; } = string.Empty;

    [JsonPropertyName("sourceVersion")]
    public string SourceVersion { get; set; } = string.Empty;

    [JsonPropertyName("sourceApi")]
    public SourceApi SourceApi { get; set; } = SourceApi.RestV2;

    [JsonPropertyName("destinationServerUrl")]
    public string DestinationServerUrl { get; set; } = string.Empty;

    [JsonPropertyName("destinationUserEmail")]
    public string DestinationUserEmail { get; set; } = string.Empty;

    [JsonPropertyName("credentialMode")]
    public CredentialMode CredentialMode { get; set; } = CredentialMode.InMemory;

    [JsonPropertyName("createdUtc")]
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("updatedUtc")]
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("createdBy")]
    public string CreatedBy { get; set; } = string.Empty;

    [JsonPropertyName("stagingPath")]
    public string StagingPath { get; set; } = string.Empty;

    [JsonIgnore]
    public ICollection<CatalogItem> CatalogItems { get; set; } = new List<CatalogItem>();
}
