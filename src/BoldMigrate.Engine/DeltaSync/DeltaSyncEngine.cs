using System.Security.Cryptography;
using BoldMigrate.Core.Enums;
using BoldMigrate.Core.Interfaces;
using BoldMigrate.Core.Models;
using BoldMigrate.Engine.Logging;
using BoldMigrate.Engine.Transform;
using BoldMigrate.Engine.Upload;
using BoldMigrate.Engine.Validation;

namespace BoldMigrate.Engine.DeltaSync;

public class DeltaSyncResult
{
    public int ItemsChecked { get; set; }
    public int ItemsChanged { get; set; }
    public int ItemsDeleted { get; set; }
    public int ItemsRevalidated { get; set; }
}

public class DeltaSyncEngine
{
    private readonly IMigrationStore _store;
    private readonly IStagingStore _staging;
    private readonly TransformEngine _transform;
    private readonly UploadLayer _upload;
    private readonly ValidationEngine _validation;
    private readonly MigrationLogger _logger;

    public DeltaSyncEngine(
        IMigrationStore store,
        IStagingStore staging,
        TransformEngine transform,
        UploadLayer upload,
        ValidationEngine validation,
        MigrationLogger logger)
    {
        _store = store;
        _staging = staging;
        _transform = transform;
        _upload = upload;
        _validation = validation;
        _logger = logger;
    }

    public async Task<DeltaSyncResult> RunDeltaSyncAsync(
        Guid runId,
        ISourceCatalog source,
        DestinationClient destClient,
        CancellationToken ct = default)
    {
        var result = new DeltaSyncResult();
        _logger.Log(Microsoft.Extensions.Logging.LogLevel.Information, "CUT-DELTA", runId, "Starting delta sync comparison against source server.");

        var existingItems = (await _store.GetCatalogItemsAsync(runId, ct: ct))
            .ToDictionary(i => i.SourcePath, StringComparer.OrdinalIgnoreCase);

        var freshItems = await source.ListItemsAsync(ct);
        var freshPaths = new HashSet<string>(freshItems.Select(f => f.Path), StringComparer.OrdinalIgnoreCase);

        foreach (var fresh in freshItems)
        {
            result.ItemsChecked++;
            if (!existingItems.TryGetValue(fresh.Path, out var stored))
            {
                // New item added on source
                continue;
            }

            if (fresh.ModifiedUtc > stored.ModifiedUtc)
            {
                // A report edited in the local designer is the migration's version now;
                // re-staging it from the source would silently discard that edit.
                if (stored.ItemType == ItemType.Report &&
                    (await _store.GetReportRecordAsync(stored.Id, ct))?.LocalEditUtc != null)
                {
                    _logger.Log(Microsoft.Extensions.Logging.LogLevel.Warning, "CUT-DELTA-EDIT", runId,
                        $"'{stored.Name}' changed on the source server but was edited locally in Step 4. "
                        + "The local edit was kept; merge the source change manually if it is needed.",
                        itemPath: stored.SourcePath);
                    continue;
                }
                var content = await source.GetDefinitionAsync(fresh.Id, ct);
                var newHash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

                if (!string.Equals(newHash, stored.Hash, StringComparison.OrdinalIgnoreCase))
                {
                    result.ItemsChanged++;
                    // Content genuinely changed -> restage, retransform, reupload, revalidate
                    var staged = await _staging.StageFileAsync(runId, fresh.Path, content, ct);
                    stored.StagedFilePath = staged;
                    stored.Hash = newHash;
                    stored.ModifiedUtc = fresh.ModifiedUtc;
                    await _store.UpdateCatalogItemAsync(stored, ct);

                    if (stored.ItemType == ItemType.Report)
                    {
                        var record = await _store.GetReportRecordAsync(stored.Id, ct);
                        if (record != null)
                        {
                            var xfrm = _transform.TransformAndClassify(stored.Id, content);
                            if (xfrm.Success)
                            {
                                await _staging.StageFileAsync(runId, fresh.Path, xfrm.RewrittenBytes, ct);
                            }

                            await _validation.ValidateReportAsync(runId, stored, record, source, destClient, ct);
                            result.ItemsRevalidated++;
                        }
                    }
                }
            }
        }

        // Detect deletions on source
        foreach (var storedPath in existingItems.Keys)
        {
            if (!freshPaths.Contains(storedPath))
            {
                result.ItemsDeleted++;
            }
        }

        _logger.Log(
            Microsoft.Extensions.Logging.LogLevel.Information,
            "CUT-DELTA",
            runId,
            $"Delta sync complete. Checked: {result.ItemsChecked}, Changed: {result.ItemsChanged}, Deleted: {result.ItemsDeleted}, Re-validated: {result.ItemsRevalidated}");

        return result;
    }
}
