using System.Text;
using BoldMigrate.Core.Interfaces;
using BoldMigrate.Engine.Preview;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace BoldMigrate.Api.Preview;

/// <summary>
/// Renders a staged report locally using StagedReportServer / ReportWriter for pre-upload validation.
/// </summary>
public class StagedReportRenderer : IStagedReportRenderer
{
    private readonly StagedReportSession _session;
    private readonly IMemoryCache _cache;
    private readonly ILogger<StagedReportRenderer> _logger;

    public StagedReportRenderer(
        StagedReportSession session,
        IMemoryCache cache,
        ILogger<StagedReportRenderer> logger)
    {
        _session = session;
        _cache = cache;
        _logger = logger;
    }

    public async Task<byte[]?> RenderStagedReportAsync(
        Guid runId,
        string reportPath,
        string format,
        IDictionary<string, string> parameters,
        CancellationToken ct = default)
    {
        try
        {
            var server = await _session.CreateServerAsync(runId, ct);
            if (server == null)
            {
                _logger.LogWarning("No staged report server found for run {RunId}", runId);
                return null;
            }

            var writer = new BoldReports.Writer.ReportWriter();
            writer.ReportingServer = server;
            writer.ReportPath = reportPath;

            if (parameters is { Count: > 0 })
            {
                var reportParams = parameters
                    .Select(kvp => new BoldReports.Web.ReportParameter
                    {
                        Name = kvp.Key,
                        Values = new List<string> { kvp.Value }
                    })
                    .ToList();
                writer.SetParameters(reportParams);
            }

            var writerFormat = format.Equals("CSV", StringComparison.OrdinalIgnoreCase)
                ? BoldReports.Writer.WriterFormat.CSV
                : BoldReports.Writer.WriterFormat.PDF;

            using var memoryStream = new MemoryStream();
            writer.Save(memoryStream, writerFormat);
            return memoryStream.ToArray();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to render staged report '{ReportPath}' locally: {Message}", reportPath, ex.Message);
            return null;
        }
    }
}
