using BoldMigrate.Engine.Transform;
using Microsoft.AspNetCore.Mvc;

namespace BoldMigrate.Api.Controllers;

[ApiController]
[Route("api/scanner")]
public class ScannerController : ControllerBase
{
    private readonly TransformEngine _transformEngine;

    public ScannerController(TransformEngine transformEngine)
    {
        _transformEngine = transformEngine;
    }

    private const long MaxRdlUploadBytes = 25 * 1024 * 1024;
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase) { ".rdl", ".rdlc", ".xml" };

    [HttpPost("analyse")]
    [RequestSizeLimit(MaxRdlUploadBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxRdlUploadBytes)]
    public async Task<IActionResult> AnalyseFile(IFormFile file, CancellationToken ct)
    {
        if (file == null || file.Length == 0)
        {
            return BadRequest(new { message = "No file supplied." });
        }

        if (file.Length > MaxRdlUploadBytes)
        {
            return BadRequest(new { message = $"File exceeds the {MaxRdlUploadBytes / (1024 * 1024)} MB limit." });
        }

        var ext = Path.GetExtension(file.FileName ?? string.Empty);
        if (!string.IsNullOrEmpty(ext) && !AllowedExtensions.Contains(ext))
        {
            return BadRequest(new { message = $"Unsupported file extension '{ext}'. Only .rdl, .rdlc, or .xml files are supported." });
        }

        using var ms = new MemoryStream();
        await file.CopyToAsync(ms, ct);
        var bytes = ms.ToArray();

        var result = _transformEngine.TransformAndClassify(Guid.NewGuid(), bytes);

        return Ok(new
        {
            fileName = file.FileName,
            sizeBytes = file.Length,
            schemaVersion = result.SchemaVersion,
            bucket = result.Bucket.ToString(),
            blockReasonCode = result.BlockReasonCode,
            message = result.Message,
            actions = result.Actions.Select(a => new { a.ActionCode, a.Before, a.After })
        });
    }
}
