using System.Text.Json;
using System.Text.Json.Serialization;
using BoldMigrate.Core.Configuration;
using BoldMigrate.Engine.Transform;
using Microsoft.AspNetCore.Mvc;

namespace BoldMigrate.Api.Controllers;

[ApiController]
[Route("api/settings")]
public class SettingsController : ControllerBase
{
    private readonly MigrationSettings _settings;
    private readonly CompatibilityRulesLoader _rulesLoader;

    public SettingsController(MigrationSettings settings, CompatibilityRulesLoader rulesLoader)
    {
        _settings = settings;
        _rulesLoader = rulesLoader;
    }

    [HttpGet]
    public IActionResult GetSettings()
    {
        return Ok(_settings);
    }

    [HttpGet("rules")]
    public IActionResult GetRules()
    {
        return Ok(_rulesLoader.LoadRules());
    }

    [HttpPut]
    public IActionResult UpdateSettings([FromBody] MigrationSettings newSettings)
    {
        if (newSettings == null)
        {
            return BadRequest(new { message = "Settings payload is required." });
        }

        var candidate = new MigrationSettings
        {
            StagingRootPath = _settings.StagingRootPath,
            StagingDeleteOnClose = newSettings.StagingDeleteOnClose,
            ExtractionPreferredApi = string.IsNullOrWhiteSpace(newSettings.ExtractionPreferredApi)
                ? _settings.ExtractionPreferredApi
                : newSettings.ExtractionPreferredApi.Trim(),
            ExtractionTimeoutSeconds = _settings.ExtractionTimeoutSeconds,
            ExtractionMaxParallel = newSettings.ExtractionMaxParallel,
            TransformMaxAutoFixAttempts = _settings.TransformMaxAutoFixAttempts,
            TransformRulesUrl = _settings.TransformRulesUrl,
            TransformRulesRefreshHours = _settings.TransformRulesRefreshHours,
            UploadMaxParallel = newSettings.UploadMaxParallel,
            UploadMaxAttempts = _settings.UploadMaxAttempts,
            UploadRetryBackoffSeconds = _settings.UploadRetryBackoffSeconds,
            ValidationDataAccuracyThreshold = newSettings.ValidationDataAccuracyThreshold,
            ValidationVisualSimilarityThreshold = newSettings.ValidationVisualSimilarityThreshold,
            ValidationPixelTolerance = newSettings.ValidationPixelTolerance,
            ValidationSampleRowLimit = newSettings.ValidationSampleRowLimit,
            ValidationMaxParallel = _settings.ValidationMaxParallel,
            ValidationRenderDpi = _settings.ValidationRenderDpi,
            UrlShimEnabled = newSettings.UrlShimEnabled,
            UrlShimListenPath = newSettings.UrlShimListenPath,
            PreviewAllowUntrustedSqlCert = newSettings.PreviewAllowUntrustedSqlCert
        };

        var (valid, error) = candidate.ValidateAndNormalize();
        if (!valid)
        {
            return BadRequest(new { message = error ?? "Invalid settings." });
        }

        ApplyMutableSettings(candidate, _settings);
        SavePersistedSettings(_settings);
        return Ok(_settings);
    }

    public static string ResolveDataDirectory(MigrationSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.DatabasePath))
        {
            return Path.GetDirectoryName(Path.GetFullPath(settings.DatabasePath)) ?? AppContext.BaseDirectory;
        }

        if (Directory.Exists("/app/data"))
        {
            return "/app/data";
        }

        return AppContext.BaseDirectory;
    }

    public static string ResolveSettingsFilePath(MigrationSettings settings) =>
        Path.Combine(ResolveDataDirectory(settings), "settings.json");

    public static void LoadPersistedSettings(MigrationSettings settings)
    {
        var path = ResolveSettingsFilePath(settings);
        if (!System.IO.File.Exists(path)) return;

        try
        {
            var json = System.IO.File.ReadAllText(path);
            var persisted = JsonSerializer.Deserialize<PersistedSettingsDto>(json);
            if (persisted == null) return;

            if (persisted.ValidationDataAccuracyThreshold.HasValue)
                settings.ValidationDataAccuracyThreshold = persisted.ValidationDataAccuracyThreshold.Value;
            if (persisted.ValidationVisualSimilarityThreshold.HasValue)
                settings.ValidationVisualSimilarityThreshold = persisted.ValidationVisualSimilarityThreshold.Value;
            if (persisted.ValidationPixelTolerance.HasValue)
                settings.ValidationPixelTolerance = persisted.ValidationPixelTolerance.Value;
            if (persisted.ValidationSampleRowLimit.HasValue)
                settings.ValidationSampleRowLimit = persisted.ValidationSampleRowLimit.Value;
            if (persisted.ExtractionMaxParallel.HasValue)
                settings.ExtractionMaxParallel = persisted.ExtractionMaxParallel.Value;
            if (!string.IsNullOrWhiteSpace(persisted.ExtractionPreferredApi))
                settings.ExtractionPreferredApi = persisted.ExtractionPreferredApi;
            if (persisted.UploadMaxParallel.HasValue)
                settings.UploadMaxParallel = persisted.UploadMaxParallel.Value;
            if (persisted.StagingDeleteOnClose.HasValue)
                settings.StagingDeleteOnClose = persisted.StagingDeleteOnClose.Value;
            if (persisted.UrlShimEnabled.HasValue)
                settings.UrlShimEnabled = persisted.UrlShimEnabled.Value;
            if (!string.IsNullOrWhiteSpace(persisted.UrlShimListenPath))
                settings.UrlShimListenPath = persisted.UrlShimListenPath;
            if (persisted.PreviewAllowUntrustedSqlCert.HasValue)
                settings.PreviewAllowUntrustedSqlCert = persisted.PreviewAllowUntrustedSqlCert.Value;

            settings.ValidateAndNormalize();
        }
        catch
        {
            // Ignore malformed persisted settings file so startup stays resilient
        }
    }

    private static void SavePersistedSettings(MigrationSettings settings)
    {
        try
        {
            var path = ResolveSettingsFilePath(settings);
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var dto = new PersistedSettingsDto
            {
                ValidationDataAccuracyThreshold = settings.ValidationDataAccuracyThreshold,
                ValidationVisualSimilarityThreshold = settings.ValidationVisualSimilarityThreshold,
                ValidationPixelTolerance = settings.ValidationPixelTolerance,
                ValidationSampleRowLimit = settings.ValidationSampleRowLimit,
                ExtractionMaxParallel = settings.ExtractionMaxParallel,
                ExtractionPreferredApi = settings.ExtractionPreferredApi,
                UploadMaxParallel = settings.UploadMaxParallel,
                StagingDeleteOnClose = settings.StagingDeleteOnClose,
                UrlShimEnabled = settings.UrlShimEnabled,
                UrlShimListenPath = settings.UrlShimListenPath,
                PreviewAllowUntrustedSqlCert = settings.PreviewAllowUntrustedSqlCert
            };
            var json = JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true });
            System.IO.File.WriteAllText(path, json);
        }
        catch
        {
            // Keep running even if read-only volume prevents persistence
        }
    }

    private static void ApplyMutableSettings(MigrationSettings src, MigrationSettings dst)
    {
        dst.ValidationDataAccuracyThreshold = src.ValidationDataAccuracyThreshold;
        dst.ValidationVisualSimilarityThreshold = src.ValidationVisualSimilarityThreshold;
        dst.ValidationPixelTolerance = src.ValidationPixelTolerance;
        dst.ValidationSampleRowLimit = src.ValidationSampleRowLimit;
        dst.ExtractionMaxParallel = src.ExtractionMaxParallel;
        dst.ExtractionPreferredApi = src.ExtractionPreferredApi;
        dst.UploadMaxParallel = src.UploadMaxParallel;
        dst.StagingDeleteOnClose = src.StagingDeleteOnClose;
        dst.UrlShimEnabled = src.UrlShimEnabled;
        dst.UrlShimListenPath = src.UrlShimListenPath;
        dst.PreviewAllowUntrustedSqlCert = src.PreviewAllowUntrustedSqlCert;
    }

    private sealed class PersistedSettingsDto
    {
        [JsonPropertyName("Validation.DataAccuracyThreshold")]
        public decimal? ValidationDataAccuracyThreshold { get; set; }
        [JsonPropertyName("Validation.VisualSimilarityThreshold")]
        public decimal? ValidationVisualSimilarityThreshold { get; set; }
        [JsonPropertyName("Validation.PixelTolerance")]
        public int? ValidationPixelTolerance { get; set; }
        [JsonPropertyName("Validation.SampleRowLimit")]
        public int? ValidationSampleRowLimit { get; set; }
        [JsonPropertyName("Extraction.MaxParallel")]
        public int? ExtractionMaxParallel { get; set; }
        [JsonPropertyName("Extraction.PreferredApi")]
        public string? ExtractionPreferredApi { get; set; }
        [JsonPropertyName("Upload.MaxParallel")]
        public int? UploadMaxParallel { get; set; }
        [JsonPropertyName("Staging.DeleteOnClose")]
        public bool? StagingDeleteOnClose { get; set; }
        [JsonPropertyName("UrlShim.Enabled")]
        public bool? UrlShimEnabled { get; set; }
        [JsonPropertyName("UrlShim.ListenPath")]
        public string? UrlShimListenPath { get; set; }
        [JsonPropertyName("Preview.AllowUntrustedSqlCert")]
        public bool? PreviewAllowUntrustedSqlCert { get; set; }
    }
}
