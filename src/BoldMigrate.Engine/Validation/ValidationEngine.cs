using System.Security.Cryptography;
using System.Text;
using BoldMigrate.Core.Configuration;
using BoldMigrate.Core.Enums;
using BoldMigrate.Core.Interfaces;
using BoldMigrate.Core.Models;
using BoldMigrate.Core.StateMachine;
using BoldMigrate.Engine.Logging;

namespace BoldMigrate.Engine.Validation;

public class ValidationEngine
{
    private readonly IMigrationStore _store;
    private readonly IStagingStore _staging;
    private readonly MigrationLogger _logger;
    private readonly MigrationSettings _settings;
    private readonly IStagedReportRenderer? _stagedRenderer;
    private bool _calibrated;

    public ValidationEngine(
        IMigrationStore store,
        IStagingStore staging,
        MigrationLogger logger,
        MigrationSettings? settings = null,
        IStagedReportRenderer? stagedRenderer = null)
    {
        _store = store;
        _staging = staging;
        _logger = logger;
        _settings = settings ?? new MigrationSettings();
        _stagedRenderer = stagedRenderer;
    }

    public static string ComputeParameterSetHash(IDictionary<string, string> parameters)
    {
        var canonical = string.Join("&", parameters.OrderBy(p => p.Key).Select(p => $"{p.Key}={p.Value}"));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Records that a validation pass has begun. Font availability on the destination server
    /// cannot be probed over the Bold Reports REST API, so this deliberately makes no claim
    /// about font parity rather than reporting a fabricated calibration score.
    /// </summary>
    public Task<bool> RunFontCalibrationAsync(Guid runId, ISourceCatalog source, CancellationToken ct = default)
    {
        if (_calibrated) return Task.FromResult(true);
        _calibrated = true;

        _logger.Log(
            Microsoft.Extensions.Logging.LogLevel.Information,
            "VALD-CALIBRATE",
            runId,
            "Starting render comparison pass. Note: destination font availability is not probed; "
            + "check fonts manually if text layout differs.");

        return Task.FromResult(true);
    }

    public Task ValidateReportAsync(
        Guid runId,
        CatalogItem reportItem,
        ReportRecord reportRecord,
        ISourceCatalog source,
        BoldMigrate.Engine.Upload.DestinationClient? destination,
        CancellationToken ct) =>
        ValidateReportAsync(runId, reportItem, reportRecord, source, destination, stagedRendererOverride: null, ct);

    public async Task ValidateReportAsync(
        Guid runId,
        CatalogItem reportItem,
        ReportRecord reportRecord,
        ISourceCatalog source,
        BoldMigrate.Engine.Upload.DestinationClient? destination = null,
        IStagedReportRenderer? stagedRendererOverride = null,
        CancellationToken ct = default)
    {
        // Step 4 Validation: verify report is staged locally or exists on destination
        var stagedExists = !string.IsNullOrEmpty(reportItem.StagedFilePath) && File.Exists(reportItem.StagedFilePath);
        if (!stagedExists)
        {
            var fallbackPath = Path.Combine(_staging.GetRunStagingPath(runId), reportItem.SourcePath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
            if (!fallbackPath.EndsWith(".rdl", StringComparison.OrdinalIgnoreCase))
            {
                fallbackPath += ".rdl";
            }
            if (File.Exists(fallbackPath))
            {
                reportItem.StagedFilePath = fallbackPath;
                stagedExists = true;
            }
        }

        var destinationUploaded = destination != null &&
            !string.IsNullOrEmpty(reportItem.DestinationItemId) &&
            !reportItem.DestinationItemId.StartsWith("dest-") &&
            !reportItem.DestinationItemId.StartsWith("rep-");

        if (!stagedExists && !destinationUploaded)
        {
            reportRecord.State = ReportState.AwaitingReview;
            await _store.UpdateReportRecordAsync(reportRecord, ct);
            _logger.Log(
                Microsoft.Extensions.Logging.LogLevel.Warning,
                "VALD-NO-STAGED-FILE",
                runId,
                $"Cannot validate '{reportItem.Name}' because its staged definition file is missing. Complete Step 2 discovery/staging first.",
                itemPath: reportItem.SourcePath,
                outcome: "StagedFileMissing");
            return;
        }

        await RunFontCalibrationAsync(runId, source, ct);

        reportRecord.State = ReportState.Validating;
        await _store.UpdateReportRecordAsync(reportRecord, ct);

        var paramDict = new Dictionary<string, string>();
        var paramHash = ComputeParameterSetHash(paramDict);

        // Render the report on SSRS source
        byte[] sourceBytes;
        try
        {
            // URL access needs the catalog path, not the item GUID.
            sourceBytes = await source.RenderAsync(reportItem.SourcePath, "CSV", paramDict, ct);
        }
        catch (Exception ex)
        {
            reportRecord.State = ReportState.AwaitingReview;
            await _store.UpdateReportRecordAsync(reportRecord, ct);
            _logger.Log(
                Microsoft.Extensions.Logging.LogLevel.Warning,
                "VALD-NO-SOURCE-RENDER",
                runId,
                $"Cannot validate '{reportItem.Name}': the source server failed to render it ({ex.Message}). Manual review required.",
                itemPath: reportItem.SourcePath,
                outcome: "SourceRenderFailed");
            return;
        }

        // Render staged report locally via IStagedReportRenderer (Step 4 pre-upload) or Bold Reports destination (Step 5 post-upload)
        byte[]? destBytes = null;
        var renderer = stagedRendererOverride ?? _stagedRenderer;

        if (renderer != null && !string.IsNullOrEmpty(reportItem.StagedFilePath) && File.Exists(reportItem.StagedFilePath))
        {
            try
            {
                destBytes = await renderer.RenderStagedReportAsync(runId, reportItem.SourcePath, "CSV", paramDict, ct);
            }
            catch (Exception ex)
            {
                _logger.Log(
                    Microsoft.Extensions.Logging.LogLevel.Warning,
                    "VALD-STAGED-RENDER-ERR",
                    runId,
                    $"Local render of staged report '{reportItem.Name}' encountered error: {ex.Message}. Falling back to destination if available.",
                    itemPath: reportItem.SourcePath,
                    outcome: "StagedRenderError");
            }
        }

        if ((destBytes == null || destBytes.Length == 0) && destinationUploaded)
        {
            destBytes = await destination!.ExportReportAsync(
                destination.GetTenantId(),
                reportItem.DestinationItemId!,
                "CSV",
                paramDict,
                ct);
        }

        if (destBytes == null || destBytes.Length == 0)
        {
            // Fail closed: an unrenderable report copy is routed to review queue,
            // never an automatic pass.
            reportRecord.State = ReportState.AwaitingReview;
            await _store.UpdateReportRecordAsync(reportRecord, ct);
            _logger.Log(
                Microsoft.Extensions.Logging.LogLevel.Warning,
                "VALD-NO-DEST-RENDER",
                runId,
                $"Report '{reportItem.Name}' staged but could not be rendered for comparison. Flagged for manual review — not auto-passed.",
                itemPath: reportItem.SourcePath,
                outcome: "DestinationRenderFailed");
            return;
        }

        var sourceCsv = Encoding.UTF8.GetString(sourceBytes);
        var destCsv = Encoding.UTF8.GetString(destBytes);

        var dataComp = DataComparer.CompareCsvData(sourceCsv, destCsv, _settings.ValidationSampleRowLimit);

        // Layout is checked by rendering to PDF on both servers and comparing pagination.
        var render = await CompareRendersAsync(runId, reportItem, source, destination, renderer, paramDict, ct);

        var result = new ValidationResult
        {
            Id = Guid.NewGuid(),
            ReportId = reportItem.Id,
            DataAccuracy = dataComp.AccuracyPercentage,
            VisualSimilarity = null,
            RowsSource = dataComp.RowsSource,
            RowsDestination = dataComp.RowsDestination,
            LayoutShifts = 0,
            SourcePageCount = render?.SourcePageCount,
            DestinationPageCount = render?.DestinationPageCount,
            RenderSummary = render?.Summary
                ?? "Neither server returned a PDF render, so layout was not compared.",
            ParameterSetHash = paramHash,
            RunUtc = DateTime.UtcNow
        };

        await _store.AddValidationResultAsync(result, ct);

        var passedData = dataComp.AccuracyPercentage >= (decimal)_settings.ValidationDataAccuracyThreshold;
        var pagesComparable = render is { SourcePageCount: not null, DestinationPageCount: not null };
        var pagesMatch = render?.PageCountMatched == true;

        if (!passedData)
        {
            reportRecord.State = ReportState.AwaitingReview;
            _logger.Log(
                Microsoft.Extensions.Logging.LogLevel.Warning,
                "VALD-RESULT",
                runId,
                $"Report '{reportItem.Name}' routed to review queue: data accuracy {dataComp.AccuracyPercentage}% "
                + $"is below the {_settings.ValidationDataAccuracyThreshold}% threshold "
                + $"(rows {dataComp.RowsDestination}/{dataComp.RowsSource}).",
                itemPath: reportItem.SourcePath,
                outcome: "AwaitingReview");
        }
        else if (!pagesComparable)
        {
            reportRecord.State = ReportState.AwaitingReview;
            _logger.Log(
                Microsoft.Extensions.Logging.LogLevel.Warning,
                "VALD-RESULT",
                runId,
                $"Report '{reportItem.Name}' matched on data ({dataComp.AccuracyPercentage}%) but layout could "
                + $"not be compared. {result.RenderSummary} Review the layout manually.",
                itemPath: reportItem.SourcePath,
                outcome: "AwaitingReview");
        }
        else if (!pagesMatch)
        {
            reportRecord.State = ReportState.AwaitingReview;
            _logger.Log(
                Microsoft.Extensions.Logging.LogLevel.Warning,
                "VALD-RESULT",
                runId,
                $"Report '{reportItem.Name}' routed to review queue. Data matched "
                + $"({dataComp.AccuracyPercentage}%) but {result.RenderSummary}",
                itemPath: reportItem.SourcePath,
                outcome: "AwaitingReview");
        }
        else
        {
            reportRecord.State = ReportState.AutoPassed;
            _logger.Log(
                Microsoft.Extensions.Logging.LogLevel.Information,
                "VALD-RESULT",
                runId,
                $"Report '{reportItem.Name}' auto-passed. Data accuracy {dataComp.AccuracyPercentage}% "
                + $"(rows {dataComp.RowsDestination}/{dataComp.RowsSource}); {result.RenderSummary} "
                + "Pixel-level layout was not compared.",
                itemPath: reportItem.SourcePath,
                outcome: "AutoPassed");
        }

        await _store.UpdateReportRecordAsync(reportRecord, ct);
    }

    /// <summary>
    /// Renders the report to PDF on both servers and compares pagination. Returns null when
    /// neither render could be obtained.
    /// </summary>
    private async Task<RenderComparisonResult?> CompareRendersAsync(
        Guid runId,
        CatalogItem reportItem,
        ISourceCatalog source,
        BoldMigrate.Engine.Upload.DestinationClient? destination,
        IStagedReportRenderer? renderer,
        IDictionary<string, string> paramDict,
        CancellationToken ct)
    {
        byte[]? sourcePdf = null;
        try
        {
            sourcePdf = await source.RenderAsync(reportItem.SourcePath, "PDF", paramDict, ct);
        }
        catch
        {
            // Leave null; the comparison reports which side was missing.
        }

        byte[]? destPdf = null;
        if (renderer != null && !string.IsNullOrEmpty(reportItem.StagedFilePath) && File.Exists(reportItem.StagedFilePath))
        {
            try
            {
                destPdf = await renderer.RenderStagedReportAsync(runId, reportItem.SourcePath, "PDF", paramDict, ct);
            }
            catch
            {
                // Fall back to destination
            }
        }

        if ((destPdf == null || destPdf.Length == 0) &&
            destination != null &&
            !string.IsNullOrEmpty(reportItem.DestinationItemId) &&
            !reportItem.DestinationItemId.StartsWith("dest-") &&
            !reportItem.DestinationItemId.StartsWith("rep-"))
        {
            destPdf = await destination.ExportReportAsync(
                destination.GetTenantId(), reportItem.DestinationItemId, "PDF", paramDict, ct);
        }

        if ((sourcePdf == null || sourcePdf.Length == 0) && (destPdf == null || destPdf.Length == 0))
        {
            return null;
        }

        return PdfStructureComparer.Compare(sourcePdf ?? Array.Empty<byte>(), destPdf ?? Array.Empty<byte>());
    }
}
