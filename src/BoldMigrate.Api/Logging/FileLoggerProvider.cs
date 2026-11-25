using System.Text;
using BoldMigrate.Engine.Logging;

namespace BoldMigrate.Api.Logging;

/// <summary>Writes every log entry to a daily text file: {directory}/boldmigrate-yyyyMMdd.log</summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _directory;
    private readonly object _lock = new();

    public FileLoggerProvider(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(_directory);
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose() { }

    private void Write(string line)
    {
        var path = Path.Combine(_directory, $"boldmigrate-{DateTime.UtcNow:yyyyMMdd}.log");
        lock (_lock)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                using var writer = new StreamWriter(stream, Encoding.UTF8);
                writer.WriteLine(line);
            }
            catch (IOException)
            {
                // Never let logging failures break the app.
            }
        }
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var message = Redactor.RedactSecrets(formatter(state, exception));
            var sb = new StringBuilder();
            sb.Append(DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append("Z [")
              .Append(logLevel.ToString().ToUpperInvariant()).Append("] ")
              .Append(category).Append(": ").Append(message);
            if (exception != null) sb.AppendLine().Append(Redactor.RedactSecrets(exception.ToString()));
            provider.Write(sb.ToString());
        }
    }
}
