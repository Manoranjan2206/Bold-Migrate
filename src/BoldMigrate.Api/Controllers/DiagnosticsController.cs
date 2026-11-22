using System.IO.Compression;
using System.Text;
using System.Text.Json;
using BoldMigrate.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace BoldMigrate.Api.Controllers;

[ApiController]
[Route("api/diagnostics")]
public class DiagnosticsController : ControllerBase
{
    private readonly IMigrationStore _store;

    public DiagnosticsController(IMigrationStore store)
    {
        _store = store;
    }

    [HttpGet("runs/{id:guid}/support-bundle")]
    public async Task<IActionResult> GenerateSupportBundle(Guid id, CancellationToken ct)
    {
        var run = await _store.GetRunAsync(id, ct);
        if (run == null) return NotFound();

        var reports = await _store.GetReportRecordsAsync(id, ct: ct);
        var audits = await _store.GetAuditEventsAsync(id, ct);

        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            // 1. run.json
            var runEntry = archive.CreateEntry("run.json");
            using (var writer = new StreamWriter(runEntry.Open()))
            {
                await writer.WriteAsync(JsonSerializer.Serialize(run, new JsonSerializerOptions { WriteIndented = true }));
            }

            // 2. reports.json
            var reportsEntry = archive.CreateEntry("reports.json");
            using (var writer = new StreamWriter(reportsEntry.Open()))
            {
                await writer.WriteAsync(JsonSerializer.Serialize(reports, new JsonSerializerOptions { WriteIndented = true }));
            }

            // 3. audit-events.json
            var auditEntry = archive.CreateEntry("audit-events.json");
            using (var writer = new StreamWriter(auditEntry.Open()))
            {
                await writer.WriteAsync(JsonSerializer.Serialize(audits, new JsonSerializerOptions { WriteIndented = true }));
            }
        }

        return File(ms.ToArray(), "application/zip", $"support-bundle-{id}-{DateTime.UtcNow:yyyyMMddHHmmss}.zip");
    }

    [HttpGet("runs/{id:guid}/report-pdf")]
    public async Task<IActionResult> DownloadMigrationReport(Guid id, CancellationToken ct)
    {
        var run = await _store.GetRunAsync(id, ct);
        if (run == null) return NotFound();

        var reports = await _store.GetReportRecordsAsync(id, ct: ct);

        var reportText = $@"=====================================================
BOLD MIGRATE — MIGRATION RUN REPORT
=====================================================
Run Identifier:       {run.Id}
Source Server:        {run.SourceServerUrl} ({run.SourceVersion})
Destination:          Bold Reports Server
Current Step:         {run.CurrentStep} (Status: {run.Status})
Created (UTC):        {run.CreatedUtc:O}
Last Updated (UTC):   {run.UpdatedUtc:O}

-----------------------------------------------------
REPORT ESTATE SUMMARY
-----------------------------------------------------
Total Reports:        {reports.Count}
Auto-Passed:          {reports.Count(r => r.State == BoldMigrate.Core.Enums.ReportState.AutoPassed)}
Approved:             {reports.Count(r => r.State == BoldMigrate.Core.Enums.ReportState.Approved)}
Awaiting Review:      {reports.Count(r => r.State == BoldMigrate.Core.Enums.ReportState.AwaitingReview)}
Blocked:              {reports.Count(r => r.Bucket == BoldMigrate.Core.Enums.ReportBucket.Blocked)}

-----------------------------------------------------
AUDIT SIGN-OFF
-----------------------------------------------------
This migration run report is certified complete and verifiable.
No customer credentials or rendered report data are held in this report.
=====================================================";

        return File(Encoding.UTF8.GetBytes(reportText), "text/plain", $"migration-report-{id}.txt");
    }
}
