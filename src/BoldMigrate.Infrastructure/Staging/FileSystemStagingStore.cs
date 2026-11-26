using BoldMigrate.Core.Interfaces;

namespace BoldMigrate.Infrastructure.Staging;

public class FileSystemStagingStore : IStagingStore
{
    private readonly string _baseStagingPath;

    public FileSystemStagingStore(string? baseStagingPath = null)
    {
        _baseStagingPath = Path.GetFullPath(baseStagingPath ?? Path.Combine(AppContext.BaseDirectory, "staging"));
        if (!Directory.Exists(_baseStagingPath))
        {
            Directory.CreateDirectory(_baseStagingPath);
        }
    }

    public string GetRunStagingPath(Guid runId)
    {
        return Path.Combine(_baseStagingPath, runId.ToString("N"));
    }

    /// <summary>
    /// Safely resolves a catalog path inside the run's staging folder, rejecting path traversal.
    /// </summary>
    public string ResolveSafeRunFilePath(Guid runId, string sourcePath)
    {
        var runPath = Path.GetFullPath(GetRunStagingPath(runId));
        var normalizedSegments = (sourcePath ?? string.Empty)
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries);

        var safeSegments = new List<string>(normalizedSegments.Length);
        foreach (var seg in normalizedSegments)
        {
            var trimmed = seg.Trim();
            if (trimmed is "" or ".") continue;
            if (trimmed == ".." || trimmed.Contains(':') || trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                throw new InvalidOperationException($"Invalid or unsafe path segment '{seg}' in '{sourcePath}'.");
            }
            safeSegments.Add(trimmed);
        }

        if (safeSegments.Count == 0)
        {
            throw new InvalidOperationException("Source path cannot be empty.");
        }

        var combined = Path.GetFullPath(Path.Combine(new[] { runPath }.Concat(safeSegments).ToArray()));
        var expectedPrefix = runPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!combined.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Resolved staging path escapes run directory for '{sourcePath}'.");
        }

        return combined;
    }

    public async Task<string> StageFileAsync(Guid runId, string sourcePath, byte[] content, CancellationToken ct = default)
    {
        var fullPath = ResolveSafeRunFilePath(runId, sourcePath);
        return await WriteCleanBytesAsync(fullPath, content, ct);
    }

    public async Task<string> StageOriginalAsync(Guid runId, string sourcePath, byte[] content, CancellationToken ct = default)
    {
        var fullPath = ResolveSafeRunFilePath(runId, "__originals/" + sourcePath.TrimStart('/', '\\'));
        return await WriteCleanBytesAsync(fullPath, content, ct);
    }

    public string? ResolveOriginalPath(Guid runId, string sourcePath)
    {
        try
        {
            var fullPath = ResolveSafeRunFilePath(runId, "__originals/" + sourcePath.TrimStart('/', '\\'));
            return File.Exists(fullPath) ? fullPath : null;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string> WriteCleanBytesAsync(string fullPath, byte[] content, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        // Strip UTF-8 BOM if present (0xEF, 0xBB, 0xBF)
        var cleanContent = content;
        if (content.Length >= 3 && content[0] == 0xEF && content[1] == 0xBB && content[2] == 0xBF)
        {
            cleanContent = new byte[content.Length - 3];
            Array.Copy(content, 3, cleanContent, 0, cleanContent.Length);
        }

        await File.WriteAllBytesAsync(fullPath, cleanContent, ct);
        return fullPath;
    }

    public async Task<byte[]> ReadStagedFileAsync(string stagedPath, CancellationToken ct = default)
    {
        var fullPath = Path.GetFullPath(stagedPath);
        var basePrefix = _baseStagingPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(basePrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Refusing to read path outside staging root: '{stagedPath}'.");
        }

        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("Staged file not found.", fullPath);
        }

        return await File.ReadAllBytesAsync(fullPath, ct);
    }

    public Task DeleteRunStagingAsync(Guid runId, CancellationToken ct = default)
    {
        var runPath = GetRunStagingPath(runId);
        if (Directory.Exists(runPath))
        {
            try
            {
                Directory.Delete(runPath, recursive: true);
            }
            catch (IOException)
            {
                // Best effort cleanup if a file handle is momentarily held
            }
        }
        return Task.CompletedTask;
    }

    public Task<long> GetAvailableSpaceAsync(CancellationToken ct = default)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(_baseStagingPath));
            if (!string.IsNullOrEmpty(root))
            {
                var drive = new DriveInfo(root);
                return Task.FromResult(drive.AvailableFreeSpace);
            }
        }
        catch
        {
            // fallback
        }
        return Task.FromResult(10L * 1024 * 1024 * 1024); // 10 GB default
    }
}
