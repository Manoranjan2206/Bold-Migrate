using System.Text.Json.Serialization;
using BoldMigrate.Core.Enums;

namespace BoldMigrate.Engine.Transform;

public class ClassificationRule
{
    [JsonPropertyName("code")]
    public string Code { get; set; } = string.Empty;

    [JsonPropertyName("xpath")]
    public string XPath { get; set; } = string.Empty;

    [JsonPropertyName("bucket")]
    public string BucketStr { get; set; } = "NeedsReview";

    [JsonIgnore]
    public ReportBucket Bucket => Enum.TryParse<ReportBucket>(BucketStr, true, out var b) ? b : ReportBucket.NeedsReview;

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("article")]
    public string Article { get; set; } = string.Empty;
}

public class RulesFileFormat
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = "2026.11.1";

    [JsonPropertyName("rules")]
    public List<ClassificationRule> Rules { get; set; } = new();
}
