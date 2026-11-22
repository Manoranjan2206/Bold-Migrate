using System.Text.Json.Serialization;

namespace BoldMigrate.Core.Configuration;

/// <summary>
/// Configuration settings for the Bold Migrate module with defaults matching the specification.
/// </summary>
public class MigrationSettings
{
    public const string SectionName = "BoldMigrate";

    /// <summary>
    /// BoldMigrate.Enabled: Hides the module entirely when false.
    /// Default: true
    /// </summary>
    [JsonPropertyName("BoldMigrate.Enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Staging.RootPath: Where downloaded files are held on disk.
    /// Default: Server data directory, "/migrations"
    /// </summary>
    [JsonPropertyName("Staging.RootPath")]
    public string StagingRootPath { get; set; } = "/migrations";

    /// <summary>
    /// Staging.DeleteOnClose: Whether staging files are deleted when the run closes.
    /// MUST NOT default to false.
    /// Default: true
    /// </summary>
    [JsonPropertyName("Staging.DeleteOnClose")]
    public bool StagingDeleteOnClose { get; set; } = true;

    /// <summary>
    /// Extraction.PreferredApi: Auto, RestV2, or Soap2010.
    /// Default: "Auto"
    /// </summary>
    [JsonPropertyName("Extraction.PreferredApi")]
    public string ExtractionPreferredApi { get; set; } = "Auto";

    /// <summary>
    /// Extraction.TimeoutSeconds: Per-request timeout against the source server (30 to 600).
    /// Default: 100
    /// </summary>
    [JsonPropertyName("Extraction.TimeoutSeconds")]
    public int ExtractionTimeoutSeconds { get; set; } = 100;

    /// <summary>
    /// Extraction.MaxParallel: Concurrent downloads (1 to 16).
    /// Default: 4
    /// </summary>
    [JsonPropertyName("Extraction.MaxParallel")]
    public int ExtractionMaxParallel { get; set; } = 4;

    /// <summary>
    /// Transform.MaxAutoFixAttempts: Attempts before a report goes to review (1 to 5).
    /// Default: 3
    /// </summary>
    [JsonPropertyName("Transform.MaxAutoFixAttempts")]
    public int TransformMaxAutoFixAttempts { get; set; } = 3;

    /// <summary>
    /// Transform.RulesUrl: URL or file path where compatibility rules are loaded from.
    /// Default: bundled file ("rules.json")
    /// </summary>
    [JsonPropertyName("Transform.RulesUrl")]
    public string TransformRulesUrl { get; set; } = "rules.json";

    /// <summary>
    /// Transform.RulesRefreshHours: How often rules are refreshed (1 to 720).
    /// Default: 24
    /// </summary>
    [JsonPropertyName("Transform.RulesRefreshHours")]
    public int TransformRulesRefreshHours { get; set; } = 24;

    /// <summary>
    /// Upload.MaxParallel: Concurrent uploads (1 to 16).
    /// Default: 4
    /// </summary>
    [JsonPropertyName("Upload.MaxParallel")]
    public int UploadMaxParallel { get; set; } = 4;

    /// <summary>
    /// Upload.MaxAttempts: Before a report is marked failed (1 to 10).
    /// Default: 3
    /// </summary>
    [JsonPropertyName("Upload.MaxAttempts")]
    public int UploadMaxAttempts { get; set; } = 3;

    /// <summary>
    /// Upload.RetryBackoffSeconds: Fixed schedule, not exponential, so a run's duration is predictable.
    /// Default: [2, 8, 30]
    /// </summary>
    [JsonPropertyName("Upload.RetryBackoffSeconds")]
    public int[] UploadRetryBackoffSeconds { get; set; } = [2, 8, 30];

    /// <summary>
    /// Validation.DataAccuracyThreshold: Percentage at or above which a report passes automatically (90.0 to 100.0).
    /// Default: 99.5
    /// </summary>
    [JsonPropertyName("Validation.DataAccuracyThreshold")]
    public decimal ValidationDataAccuracyThreshold { get; set; } = 99.5m;

    /// <summary>
    /// Validation.VisualSimilarityThreshold: Percentage at or above which visual check passes (80.0 to 100.0).
    /// Default: 95.0
    /// </summary>
    [JsonPropertyName("Validation.VisualSimilarityThreshold")]
    public decimal ValidationVisualSimilarityThreshold { get; set; } = 95.0m;

    /// <summary>
    /// Validation.PixelTolerance: Per-channel tolerance before a pixel counts as different (0 to 10).
    /// Default: 2
    /// </summary>
    [JsonPropertyName("Validation.PixelTolerance")]
    public int ValidationPixelTolerance { get; set; } = 2;

    /// <summary>
    /// Validation.SampleRowLimit: Rows compared per report before sampling begins (1000 to 1000000).
    /// Default: 10000
    /// </summary>
    [JsonPropertyName("Validation.SampleRowLimit")]
    public int ValidationSampleRowLimit { get; set; } = 10000;

    /// <summary>
    /// Validation.MaxParallel: Concurrent renders (1 to 8). Deliberately lower than upload.
    /// Default: 2
    /// </summary>
    [JsonPropertyName("Validation.MaxParallel")]
    public int ValidationMaxParallel { get; set; } = 2;

    /// <summary>
    /// Validation.RenderDpi: MUST be identical on both servers (96 or 150).
    /// Default: 96
    /// </summary>
    [JsonPropertyName("Validation.RenderDpi")]
    public int ValidationRenderDpi { get; set; } = 96;

    /// <summary>
    /// Run.ScheduledWindow: Cron expression when heavy stages run, or null.
    /// Default: null (not set)
    /// </summary>
    [JsonPropertyName("Run.ScheduledWindow")]
    public string? RunScheduledWindow { get; set; }

    /// <summary>
    /// Logging.Level: Trace to Error.
    /// Default: "Information"
    /// </summary>
    [JsonPropertyName("Logging.Level")]
    public string LoggingLevel { get; set; } = "Information";

    /// <summary>
    /// Logging.RetentionDays: Retention period for system logs (7 to 365).
    /// Default: 90
    /// </summary>
    [JsonPropertyName("Logging.RetentionDays")]
    public int LoggingRetentionDays { get; set; } = 90;

    /// <summary>
    /// UrlShim.Enabled: Switched on explicitly at cutover.
    /// Default: false
    /// </summary>
    [JsonPropertyName("UrlShim.Enabled")]
    public bool UrlShimEnabled { get; set; } = false;

    /// <summary>
    /// UrlShim.ListenPath: Must match the old SSRS path.
    /// Default: "/ReportServer"
    /// </summary>
    [JsonPropertyName("UrlShim.ListenPath")]
    public string UrlShimListenPath { get; set; } = "/ReportServer";
}
