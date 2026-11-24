using BoldMigrate.Core;
using BoldMigrate.Engine.Logging;
using BoldMigrate.Engine.Upload;
using Microsoft.AspNetCore.Mvc;

namespace BoldMigrate.Api.Controllers;

/// <summary>Standalone tool: list and bulk-delete reports on a Bold Reports server, independent of any migration run.</summary>
[ApiController]
[Route("api/tools/bold-delete")]
public class BoldDeleteToolController : ControllerBase
{
    private readonly ILogger<BoldDeleteToolController> _log;

    public BoldDeleteToolController(ILogger<BoldDeleteToolController> log) => _log = log;

    [HttpPost("list")]
    public async Task<IActionResult> List([FromBody] BoldServerRequest req, CancellationToken ct)
    {
        var (client, tenant, error) = await ConnectAsync(req, ct);
        if (client == null) return BadRequest(new { success = false, message = error });

        try
        {
            var items = await client.GetItemsAsync(tenant, "Report", ct);
            var reports = items
                .OrderBy(i => i.CategoryName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
                .Select(i => new { id = i.Id, name = i.Name, category = i.CategoryName ?? "" });
            return Ok(new { success = true, reports });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Listing Bold Reports items failed");
            return StatusCode(502, new { success = false, message = $"Failed to list reports: {ex.Message}" });
        }
    }

    [HttpPost("delete")]
    public async Task<IActionResult> Delete([FromBody] BoldDeleteRequest req, CancellationToken ct)
    {
        if (req.ReportIds == null || req.ReportIds.Length == 0)
            return BadRequest(new { success = false, message = "Select at least one report." });

        var (client, tenant, error) = await ConnectAsync(req, ct);
        if (client == null) return BadRequest(new { success = false, message = error });

        var deleted = new List<string>();
        var failed = new List<string>();
        foreach (var id in req.ReportIds.Distinct())
        {
            try
            {
                if (await client.DeleteItemAsync(tenant, id, ct)) deleted.Add(id);
                else failed.Add(id);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Delete failed for report {ReportId}", id);
                failed.Add(id);
            }
        }

        _log.LogInformation("[BOLD-DELETE] Deleted {Deleted} report(s), {Failed} failed on {Url}", deleted.Count, failed.Count, req.Url);
        return Ok(new
        {
            success = failed.Count == 0,
            deletedCount = deleted.Count,
            failedCount = failed.Count,
            deletedIds = deleted,
            failedIds = failed,
            message = $"Deleted {deleted.Count} report(s)" + (failed.Count > 0 ? $", {failed.Count} failed." : ".")
        });
    }

    private static async Task<(DestinationClient? client, string? tenant, string? error)> ConnectAsync(BoldServerRequest req, CancellationToken ct)
    {
        if (!Uri.TryCreate(req.Url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
            return (null, null, "Enter a valid http(s) Bold Reports URL.");
        if (string.IsNullOrWhiteSpace(req.Email) || string.IsNullOrEmpty(req.Password))
            return (null, null, "Email and password are required.");

        var client = new DestinationClient(req.Url.TrimEnd('/'));
        try
        {
            var token = await client.AuthenticateAsync(req.Email, req.Password, ct);
            if (string.IsNullOrEmpty(token)) return (null, null, "Authentication failed.");
            client.SetBearerToken(token);
            return (client, client.GetTenantId(), null);
        }
        catch (Exception ex)
        {
            return (null, null, $"Authentication failed: {ex.Message}");
        }
    }
}

public record BoldServerRequest(string Url, string Email, string Password);
public record BoldDeleteRequest(string Url, string Email, string Password, string[]? ReportIds) : BoldServerRequest(Url, Email, Password);
