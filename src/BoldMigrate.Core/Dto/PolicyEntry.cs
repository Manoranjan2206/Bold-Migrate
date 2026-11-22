using System.Text.Json.Serialization;

namespace BoldMigrate.Core.Dto;

/// <summary>
/// Represents a security policy or role assignment on an item.
/// </summary>
public class PolicyEntry
{
    [JsonPropertyName("principal")]
    public string Principal { get; set; } = string.Empty;

    [JsonPropertyName("roles")]
    public IReadOnlyList<string> Roles { get; set; } = Array.Empty<string>();
}
