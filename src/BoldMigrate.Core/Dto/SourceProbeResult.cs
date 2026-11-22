using System.Text.Json.Serialization;
using BoldMigrate.Core.Enums;

namespace BoldMigrate.Core.Dto;

/// <summary>
/// Output of an SSRS source server probe operation.
/// </summary>
public class SourceProbeResult
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("sourceVersion")]
    public string? SourceVersion { get; set; }

    [JsonPropertyName("sourceApi")]
    public SourceApi SourceApi { get; set; }

    [JsonPropertyName("authMode")]
    public string? AuthMode { get; set; }

    [JsonPropertyName("errorCode")]
    public string? ErrorCode { get; set; }

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; set; }
}
