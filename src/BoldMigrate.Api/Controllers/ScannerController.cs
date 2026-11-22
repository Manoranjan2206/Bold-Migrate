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

    [HttpPost("analyse")]
    public async Task<IActionResult> AnalyseFile(IFormFile file, CancellationToken ct)
    {
        if (file == null || file.Length == 0)
        {
            return BadRequest(new { message = "No file supplied." });
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
