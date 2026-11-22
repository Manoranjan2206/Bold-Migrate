using System.Collections.Concurrent;
using BoldMigrate.Core.Interfaces;
using BoldMigrate.Core.Models;
using Microsoft.Extensions.Logging;

namespace BoldMigrate.Engine.Logging;

public class LiveLogEntry
{
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string Level { get; set; } = "Information";
    public string Message { get; set; } = string.Empty;
    public string EventCode { get; set; } = string.Empty;
}

public class MigrationLogger
{
    private readonly ILogger _systemLogger;
    private readonly IMigrationStore _store;
    private static readonly ConcurrentQueue<LiveLogEntry> _liveLogs = new();

    public MigrationLogger(ILogger systemLogger, IMigrationStore store)
    {
        _systemLogger = systemLogger;
        _store = store;
    }

    public void Log(
        LogLevel level,
        string eventCode,
        Guid runId,
        string message,
        Guid? correlationId = null,
        int? step = null,
        string? itemType = null,
        string? itemPath = null,
        int? attempt = null,
        long? durationMs = null,
        string? outcome = null,
        string? userId = null)
    {
        var redactedMessage = Redactor.RedactSecrets(message);

        // System log (structured fields)
        _systemLogger.Log(
            level,
            "[{EventCode}] [Run:{RunId}] [Corr:{CorrId}] [Step:{Step}] [Item:{ItemPath}] {Message} (Duration: {DurationMs}ms, Outcome: {Outcome})",
            eventCode,
            runId,
            correlationId ?? Guid.Empty,
            step,
            itemPath ?? "N/A",
            redactedMessage,
            durationMs ?? 0,
            outcome ?? "N/A");

        // Live log for UI (Information, Warning, Error, Critical)
        if (level >= LogLevel.Information)
        {
            var liveEntry = new LiveLogEntry
            {
                Timestamp = DateTime.UtcNow,
                Level = level.ToString(),
                Message = redactedMessage,
                EventCode = eventCode
            };
            _liveLogs.Enqueue(liveEntry);

            // Cap in-memory live logs to 500 entries
            while (_liveLogs.Count > 500)
            {
                _liveLogs.TryDequeue(out _);
            }
        }

        // Audit Trail when person or important action is involved
        if (!string.IsNullOrEmpty(userId) || eventCode.StartsWith("CUT-") || eventCode.StartsWith("VALD-APPROVE"))
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await _store.AddAuditEventAsync(new AuditEvent
                    {
                        Id = Guid.NewGuid(),
                        RunId = runId,
                        UserId = userId ?? "System",
                        Action = eventCode,
                        TargetType = itemType ?? "System",
                        TargetId = itemPath ?? runId.ToString(),
                        OccurredUtc = DateTime.UtcNow,
                        Detail = redactedMessage
                    });
                }
                catch
                {
                    // Ignore audit logging failures in background
                }
            });
        }
    }

    public IReadOnlyList<LiveLogEntry> GetLiveLog() => _liveLogs.ToArray();
}
