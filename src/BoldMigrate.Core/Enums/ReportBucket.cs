using System.Text.Json.Serialization;

namespace BoldMigrate.Core.Enums;

/// <summary>
/// Classification bucket assigned to a report during transform analysis.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ReportBucket
{
    Clean,
    AutoFixed,
    NeedsReview,
    Blocked,
    Excluded
}
