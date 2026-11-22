using System.Text.Json.Serialization;

namespace BoldMigrate.Core.Dto;

/// <summary>
/// Valid value option for a report parameter.
/// </summary>
public class ValidValueDto
{
    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    [JsonPropertyName("value")]
    public string Value { get; set; } = string.Empty;
}

/// <summary>
/// Definition of a report parameter extracted from the source server.
/// </summary>
public class ParameterDef
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("dataType")]
    public string? DataType { get; set; }

    [JsonPropertyName("prompt")]
    public string? Prompt { get; set; }

    [JsonPropertyName("nullable")]
    public bool Nullable { get; set; }

    [JsonPropertyName("multiValue")]
    public bool MultiValue { get; set; }

    [JsonPropertyName("allowBlank")]
    public bool AllowBlank { get; set; }

    [JsonPropertyName("defaultValues")]
    public IReadOnlyList<string> DefaultValues { get; set; } = Array.Empty<string>();

    [JsonPropertyName("validValues")]
    public IReadOnlyList<ValidValueDto> ValidValues { get; set; } = Array.Empty<ValidValueDto>();
}
