using BoldMigrate.Core;
using BoldMigrate.Engine.Logging;
using BoldMigrate.Engine.Upload;
using Microsoft.AspNetCore.Mvc;

namespace BoldMigrate.Api.Controllers;

/// <summary>Standalone tool: list and bulk-delete reports, datasets, data sources, and folders on a Bold Reports server, independent of any migration run.</summary>
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
            var reportsTask = client.GetItemsAsync(tenant, "Report", ct);
            var datasetsTask = client.GetItemsAsync(tenant, "Dataset", ct);
            var datasourcesTask = client.GetItemsAsync(tenant, "Datasource", ct);
            var foldersTask = client.GetItemsAsync(tenant, "Category", ct);

            await Task.WhenAll(reportsTask, datasetsTask, datasourcesTask, foldersTask);

            var allItems = new List<object>();
            var mapped = new List<(string id, string name, string category, string itemType)>();

            void AddGroup(List<DestinationItemInfo> source, string displayType)
            {
                foreach (var i in source)
                {
                    mapped.Add((i.Id, i.Name, i.CategoryName ?? "", displayType));
                }
            }

            AddGroup(reportsTask.Result, "Report");
            AddGroup(datasetsTask.Result, "Dataset");
            AddGroup(datasourcesTask.Result, "Datasource");
            AddGroup(foldersTask.Result, "Folder");

            var ordered = mapped
                .OrderBy(i => GetTypeSortOrder(i.itemType))
                .ThenBy(i => i.category, StringComparer.OrdinalIgnoreCase)
                .ThenBy(i => i.name, StringComparer.OrdinalIgnoreCase)
                .Select(i => new { id = i.id, name = i.name, category = i.category, itemType = i.itemType })
                .ToList();

            return Ok(new
            {
                success = true,
                items = ordered,
                reports = ordered.Where(i => i.itemType == "Report"),
                counts = new
                {
                    report = reportsTask.Result.Count,
                    dataset = datasetsTask.Result.Count,
                    datasource = datasourcesTask.Result.Count,
                    folder = foldersTask.Result.Count
                }
            });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Listing Bold Reports items failed");
            return StatusCode(502, new { success = false, message = $"Failed to list items: {ex.Message}" });
        }
    }

    [HttpPost("delete")]
    public async Task<IActionResult> Delete([FromBody] BoldDeleteRequest req, CancellationToken ct)
    {
        var targets = new List<(string id, string itemType)>();
        if (req.Items != null && req.Items.Length > 0)
        {
            foreach (var item in req.Items)
            {
                if (!string.IsNullOrWhiteSpace(item.Id))
                    targets.Add((item.Id, string.IsNullOrWhiteSpace(item.ItemType) ? "Report" : item.ItemType));
            }
        }
        else if (req.ReportIds != null && req.ReportIds.Length > 0)
        {
            foreach (var id in req.ReportIds)
            {
                if (!string.IsNullOrWhiteSpace(id))
                    targets.Add((id, "Report"));
            }
        }

        if (targets.Count == 0)
            return BadRequest(new { success = false, message = "Select at least one item to delete." });

        var (client, tenant, error) = await ConnectAsync(req, ct);
        if (client == null) return BadRequest(new { success = false, message = error });

        // Delete dependents before prerequisites: Reports -> Datasets -> Data Sources -> Folders
        var orderedTargets = targets
            .GroupBy(t => t.id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(t => GetTypeSortOrder(t.itemType))
            .ToList();

        var deleted = new List<string>();
        var failed = new List<string>();
        foreach (var target in orderedTargets)
        {
            try
            {
                if (await client.DeleteItemAsync(tenant, target.id, ct)) deleted.Add(target.id);
                else failed.Add(target.id);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Delete failed for {ItemType} {ItemId}", target.itemType, target.id);
                failed.Add(target.id);
            }
        }

        _log.LogInformation("[BOLD-DELETE] Deleted {Deleted} item(s), {Failed} failed on {Url}", deleted.Count, failed.Count, req.Url);
        return Ok(new
        {
            success = failed.Count == 0,
            deletedCount = deleted.Count,
            failedCount = failed.Count,
            deletedIds = deleted,
            failedIds = failed,
            message = $"Deleted {deleted.Count} item(s)" + (failed.Count > 0 ? $", {failed.Count} failed." : ".")
        });
    }

    private static int GetTypeSortOrder(string? itemType) => itemType?.ToLowerInvariant() switch
    {
        "report" => 0,
        "dataset" => 1,
        "datasource" => 2,
        "folder" or "category" => 3,
        _ => 4
    };

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
public record BoldDeleteItem(string Id, string? ItemType = null);
public record BoldDeleteRequest(string Url, string Email, string Password, string[]? ReportIds = null, BoldDeleteItem[]? Items = null) : BoldServerRequest(Url, Email, Password);
