namespace BoldMigrate.Core.Interfaces;

/// <summary>
/// Renders a staged report definition locally using local staging (e.g. StagedReportServer or local engine),
/// allowing report outputs to be validated and compared against SSRS source renders prior to destination upload.
/// </summary>
public interface IStagedReportRenderer
{
    Task<byte[]?> RenderStagedReportAsync(
        Guid runId,
        string reportPath,
        string format,
        IDictionary<string, string> parameters,
        CancellationToken ct = default);
}
