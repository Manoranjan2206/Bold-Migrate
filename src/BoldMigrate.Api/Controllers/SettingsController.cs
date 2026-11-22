using BoldMigrate.Core.Configuration;
using Microsoft.AspNetCore.Mvc;

namespace BoldMigrate.Api.Controllers;

[ApiController]
[Route("api/settings")]
public class SettingsController : ControllerBase
{
    private readonly MigrationSettings _settings;

    public SettingsController(MigrationSettings settings)
    {
        _settings = settings;
    }

    [HttpGet]
    public IActionResult GetSettings()
    {
        return Ok(_settings);
    }

    [HttpPut]
    public IActionResult UpdateSettings([FromBody] MigrationSettings newSettings)
    {
        _settings.ValidationDataAccuracyThreshold = newSettings.ValidationDataAccuracyThreshold;
        _settings.ValidationVisualSimilarityThreshold = newSettings.ValidationVisualSimilarityThreshold;
        _settings.ValidationPixelTolerance = newSettings.ValidationPixelTolerance;
        _settings.ValidationSampleRowLimit = newSettings.ValidationSampleRowLimit;
        _settings.ExtractionMaxParallel = newSettings.ExtractionMaxParallel;
        _settings.UploadMaxParallel = newSettings.UploadMaxParallel;
        _settings.UrlShimEnabled = newSettings.UrlShimEnabled;
        _settings.UrlShimListenPath = newSettings.UrlShimListenPath;
        return Ok(_settings);
    }
}
