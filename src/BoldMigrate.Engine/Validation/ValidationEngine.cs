using System.Security.Cryptography;
using System.Text;
using BoldMigrate.Core.Configuration;
using BoldMigrate.Core.Enums;
using BoldMigrate.Core.Interfaces;
using BoldMigrate.Core.Models;
using BoldMigrate.Engine.Logging;

namespace BoldMigrate.Engine.Validation;

public class ValidationEngine
{
    private readonly IMigrationStore _store;
    private readonly IStagingStore _staging;
    private readonly MigrationLogger _logger;
    private readonly MigrationSettings _settings;
    private bool _calibrated;

    public ValidationEngine(
        IMigrationStore store,
        IStagingStore staging,
        MigrationLogger logger,
        MigrationSettings? settings = null)
    {
        _store = store;
        _staging = staging;
        _logger = logger;
        _settings = settings ?? new MigrationSettings();
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

    public async Task ValidateReportAsync(
        Guid runId,
        CatalogItem reportItem,
        ReportRecord reportRecord,
        ISourceCatalog source,
        BoldMigrate.Engine.Upload.DestinationClient? destination = null,
        CancellationToken ct = default)
    {
        // Enforce that report must actually have been uploaded to destination
        if (string.IsNullOrEmpty(reportItem.DestinationItemId) ||
            reportItem.DestinationItemId.StartsWith("dest-") ||
            reportItem.DestinationItemId.StartsWith("rep-"))
        {
            reportRecord.State = ReportState.UploadFailed;
            await _store.UpdateReportRecordAsync(reportRecord, ct);
            _logger.Log(
                Microsoft.Extensions.Logging.LogLevel.Warning,
                "VALD-NO-UPLOAD",
                runId,
                $"Cannot validate '{reportItem.Name}' because it has not been uploaded to Bold Reports Server yet.",
                itemPath: reportItem.SourcePath,
                outcome: "UploadRequired");
            return;
        }

        await RunFontCalibrationAsync(runId, source, ct);

        reportRecord.State = ReportState.Validating;
        await _store.UpdateReportRecordAsync(reportRecord, ct);

        var paramDict = new Dictionary<string, string>();
        var paramHash = ComputeParameterSetHash(paramDict);

        // Render the report on BOTH servers with the same parameter set. The destination
        // render must come from Bold Reports itself — comparing the source against itself
        // would make every report pass regardless of whether the migration actually worked.
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

        if (destination == null)
        {
            reportRecord.State = ReportState.AwaitingReview;
            await _store.UpdateReportRecordAsync(reportRecord, ct);
            _logger.Log(
                Microsoft.Extensions.Logging.LogLevel.Warning,
                "VALD-NO-DEST-CLIENT",
                runId,
                $"Cannot validate '{reportItem.Name}': no authenticated Bold Reports connection is available to render the destination copy. Re-connect the destination server and re-run validation.",
                itemPath: reportItem.SourcePath,
                outcome: "DestinationUnavailable");
            return;
        }

        var destBytes = await destination.ExportReportAsync(
            destination.GetTenantId(),
            reportItem.DestinationItemId,
            "CSV",
            paramDict,
            ct);

        if (destBytes == null)
        {
            // Fail closed: an unrenderable destination copy is a validation failure,
            // never an automatic pass.
            reportRecord.State = ReportState.AwaitingReview;
            await _store.UpdateReportRecordAsync(reportRecord, ct);
            _logger.Log(
                Microsoft.Extensions.Logging.LogLevel.Warning,
                "VALD-NO-DEST-RENDER",
                runId,
                $"Report '{reportItem.Name}' uploaded but Bold Reports could not render it for comparison. Flagged for manual review — not auto-passed.",
                itemPath: reportItem.SourcePath,
                outcome: "DestinationRenderFailed");
            return;
        }

        var sourceCsv = Encoding.UTF8.GetString(sourceBytes);
        var destCsv = Encoding.UTF8.GetString(destBytes);

        var dataComp = DataComparer.CompareCsvData(sourceCsv, destCsv, _settings.ValidationSampleRowLimit);

        // Layout is checked by rendering to PDF on both servers and comparing pagination.
        // No pixel percentage is produced — see PdfStructureComparer for why that would be fiction.
        var render = await CompareRendersAsync(reportItem, source, destination, paramDict, ct);

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
        CatalogItem reportItem,
        ISourceCatalog source,
        BoldMigrate.Engine.Upload.DestinationClient destination,
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

        var destPdf = await destination.ExportReportAsync(
            destination.GetTenantId(), reportItem.DestinationItemId, "PDF", paramDict, ct);

        if ((sourcePdf == null || sourcePdf.Length == 0) && (destPdf == null || destPdf.Length == 0))
        {
            return null;
        }

        return PdfStructureComparer.Compare(sourcePdf ?? Array.Empty<byte>(), destPdf ?? Array.Empty<byte>());
    }
}
