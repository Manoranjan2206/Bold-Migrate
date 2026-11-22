using BoldMigrate.Core.Enums;
using BoldMigrate.Core.Models;

namespace BoldMigrate.Engine.Orchestrator;

public interface IMigrationOrchestrator
{
    DateTime LastHeartbeatUtc { get; }
    Task<MigrationRun> CreateOrGetActiveRunAsync(string name, string sourceUrl, CancellationToken ct = default);
    Task<MigrationRun?> GetRunAsync(Guid runId, CancellationToken ct = default);
    Task<MigrationRun?> GetActiveRunAsync(CancellationToken ct = default);
    Task StartFetchAsync(Guid runId, CancellationToken ct = default);
    Task StartCheckAsync(Guid runId, CancellationToken ct = default);
    Task StartUploadAsync(Guid runId, CancellationToken ct = default);
    void PauseRun(Guid runId);
    void ResumeRun(Guid runId);
    Task StartValidationAsync(Guid runId, CancellationToken ct = default);
    Task RetryFailedItemsAsync(Guid runId, CancellationToken ct = default);
    Task AbandonRunAsync(Guid runId, CancellationToken ct = default);
    Task CompleteCutoverAsync(Guid runId, CancellationToken ct = default);
}
