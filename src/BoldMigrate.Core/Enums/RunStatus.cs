using System.Text.Json.Serialization;

namespace BoldMigrate.Core.Enums;

/// <summary>
/// Status of a migration run.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RunStatus
{
    Draft,
    Connected,
    Discovered,
    Analysed,
    Uploading,
    Uploaded,
    Validating,
    Validated,
    CutOver,
    Abandoned
}
