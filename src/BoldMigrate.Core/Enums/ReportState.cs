using System.Text.Json.Serialization;

namespace BoldMigrate.Core.Enums;

/// <summary>
/// Lifecycle state of a single report within a migration run.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ReportState
{
    Discovered,
    Staged,
    Analysed,
    Blocked,
    Queued,
    Uploading,
    Uploaded,
    UploadFailed,
    Validating,
    AutoPassed,
    AwaitingReview,
    ValidationFailed,
    Approved,
    Flagged,
    Excluded
}
