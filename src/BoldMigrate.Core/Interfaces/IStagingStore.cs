namespace BoldMigrate.Core.Interfaces;

/// <summary>
/// Manages the run-scoped local disk staging directory holding downloaded report definitions and artifacts.
/// </summary>
public interface IStagingStore
{
    /// <summary>
    /// Writes content to the run's staging directory under a relative path mirroring sourcePath.
    /// Strips any byte order mark (BOM) before writing and returns the absolute staged path.
    /// </summary>
    Task<string> StageFileAsync(Guid runId, string sourcePath, byte[] content, CancellationToken ct = default);

    /// <summary>
    /// Reads the raw staged file bytes from local disk.
    /// </summary>
    Task<byte[]> ReadStagedFileAsync(string stagedPath, CancellationToken ct = default);

    /// <summary>
    /// Completely deletes the staging directory and all contents for a migration run.
    /// </summary>
    Task DeleteRunStagingAsync(Guid runId, CancellationToken ct = default);

    /// <summary>
    /// Gets the absolute root staging directory path for the given run.
    /// </summary>
    string GetRunStagingPath(Guid runId);

    /// <summary>
    /// Returns available disk space in bytes on the staging volume.
    /// </summary>
    Task<long> GetAvailableSpaceAsync(CancellationToken ct = default);

    /// <summary>
    /// Preserves an unmodified original copy of a staged file (for diffing after transforms).
    /// </summary>
    Task<string> StageOriginalAsync(Guid runId, string sourcePath, byte[] content, CancellationToken ct = default) =>
        StageFileAsync(runId, "__originals/" + sourcePath.TrimStart('/', '\\'), content, ct);

    /// <summary>
    /// Resolves the preserved original file path for a staged item, if present.
    /// </summary>
    string? ResolveOriginalPath(Guid runId, string sourcePath) => null;
}
