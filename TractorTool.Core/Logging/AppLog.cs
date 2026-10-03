using TractorTool.Contracts;

namespace TractorTool.Core.Logging;

public sealed class AppLog : IAppLog
{
    private readonly object _gate = new();
    private readonly Queue<string> _recent = new();
    public string FilePath { get; }
    public string? WriteError { get; private set; }
    public event Action? Changed;

    public AppLog(string directory)
    {
        Directory.CreateDirectory(directory);
        FilePath = Path.Combine(directory, $"host-{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}.log");
    }

    public void Write(string source, string message, Exception? error = null)
    {
        var line = $"{DateTimeOffset.Now:O} [{source}] {message}" + (error is null ? "" : $"\n{error}");
        lock (_gate)
        {
            _recent.Enqueue(line);
            while (_recent.Count > 300) _recent.Dequeue();
            try { File.AppendAllText(FilePath, line + Environment.NewLine); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { WriteError = ex.Message; }
        }
        Changed?.Invoke();
    }

    public string ReadRecent()
    {
        lock (_gate) return (WriteError is null ? "" : "日志写入失败：" + WriteError + "\n") + string.Join(Environment.NewLine, _recent);
    }
}
