using System.Security.Cryptography;
using BoldMigrate.Core.Interfaces;

namespace BoldMigrate.Infrastructure.Staging;

public class FileSystemStagingStore : IStagingStore
{
    private readonly string _baseStagingPath;

    public FileSystemStagingStore(string? baseStagingPath = null)
    {
        _baseStagingPath = baseStagingPath ?? Path.Combine(AppContext.BaseDirectory, "staging");
        if (!Directory.Exists(_baseStagingPath))
        {
            Directory.CreateDirectory(_baseStagingPath);
        }
    }

    public string GetRunStagingPath(Guid runId)
    {
        return Path.Combine(_baseStagingPath, runId.ToString("N"));
    }

    public async Task<string> StageFileAsync(Guid runId, string sourcePath, byte[] content, CancellationToken ct = default)
    {
        var runPath = GetRunStagingPath(runId);
        var normalizedRelPath = sourcePath.TrimStart('/', '\\').Replace('/', Path.DirectorySeparatorChar);
        var fullPath = Path.Combine(runPath, normalizedRelPath);

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
        if (!File.Exists(stagedPath))
        {
            throw new FileNotFoundException("Staged file not found.", stagedPath);
        }

        return await File.ReadAllBytesAsync(stagedPath, ct);
    }

    public Task DeleteRunStagingAsync(Guid runId, CancellationToken ct = default)
    {
        var runPath = GetRunStagingPath(runId);
        if (Directory.Exists(runPath))
        {
            Directory.Delete(runPath, recursive: true);
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
