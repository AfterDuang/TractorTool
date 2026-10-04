using TractorTool.Contracts;

namespace TractorTool.Core.Logging;

public sealed record LogEntry(long Id, DateTimeOffset Timestamp, AppLogLevel Level, string Source, string Message, string? Detail)
{
    public string LevelName => Level.ToString().ToUpperInvariant();
    public string Text => $"{Timestamp:O} [{LevelName}] [{Source}] {Message}" + (Detail is null ? "" : Environment.NewLine + Detail);
}

public sealed record LogSnapshot(IReadOnlyList<LogEntry> Entries, long UnreadErrors, string? WriteError);

public sealed class AppLog : IAppLog
{
    private readonly object _gate = new();
    private readonly Queue<LogEntry> _recent = new();
    private long _sequence, _errors, _acknowledged;
    public string FilePath { get; }
    public string? WriteError { get; private set; }
    public event Action? Changed;

    public AppLog(string directory)
    {
        Directory.CreateDirectory(directory);
        FilePath = Path.Combine(directory, $"host-{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}.log");
    }

    public void Write(string source, string message, Exception? error = null) =>
        Write(error is null ? AppLogLevel.Info : AppLogLevel.Error, source, message, error);

    public void Write(AppLogLevel level, string source, string message, Exception? error = null)
    {
        if (!Enum.IsDefined(level)) throw new ArgumentOutOfRangeException(nameof(level));
        lock (_gate)
        {
            var entry = Append(level, source, message, error?.ToString());
            try { File.AppendAllText(FilePath, entry.Text + Environment.NewLine); WriteError = null; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Surface a failed sink without recursively writing to that same sink.
                if (WriteError != ex.Message) Append(AppLogLevel.Error, "Logging", "日志文件写入失败；当前记录暂存于内存。", ex.Message);
                WriteError = ex.Message;
            }
        }
        Notify();
    }

    private LogEntry Append(AppLogLevel level, string source, string message, string? detail)
    {
        var entry = new LogEntry(++_sequence, DateTimeOffset.Now, level, source, message, detail);
        _recent.Enqueue(entry);
        while (_recent.Count > 300) _recent.Dequeue();
        if (level == AppLogLevel.Error) _errors++;
        return entry;
    }

    public LogSnapshot Snapshot()
    {
        lock (_gate) return new(_recent.ToArray(), _errors - _acknowledged, WriteError);
    }

    // Acknowledge everything collected so far, including hidden or paused records.
    public void AcknowledgeErrors()
    {
        lock (_gate) _acknowledged = _errors;
        Notify();
    }

    public string ReadRecent() => string.Join(Environment.NewLine, Snapshot().Entries.Select(e => e.Text));

    private void Notify()
    {
        foreach (Action handler in Changed?.GetInvocationList() ?? [])
            try { handler(); }
            catch { /* A display subscriber cannot break logging or the operation being recorded. */ }
    }
}
