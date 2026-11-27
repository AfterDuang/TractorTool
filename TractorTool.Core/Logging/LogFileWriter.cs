using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace TractorTool.Core.Logging;

// Called only under AppLog's lock. The host owns the data-root instance lease.
internal sealed partial class LogFileWriter
{
    internal const int MaxFileBytes = 10 * 1024 * 1024;
    internal const int MaxTotalBytes = 100 * 1024 * 1024;
    internal const int MaxFiles = 20;
    internal const int RetentionDays = 14;
    internal const int MaxEntryBytes = 64 * 1024;
    internal static string RetentionDescription =>
        $"保留最近 {RetentionDays} 天，最多 {MaxFiles} 个文件、共 {MaxTotalBytes / 1024 / 1024} MiB；单文件 {MaxFileBytes / 1024 / 1024} MiB。自动清理旧日志，正在写入的文件保留。";
    private static readonly byte[] Truncated = Encoding.UTF8.GetBytes("\n[磁盘记录超过 64 KiB，已截断；完整内容仅在本次内存记录中保留。]" + Environment.NewLine);
    private readonly string _directory;
    private readonly bool _pruneFiles;
    private readonly string _session = $"host-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Environment.ProcessId}-{Guid.NewGuid():N}";
    private int _part;
    private DateOnly? _cleanupDay;
    private bool _maintenanceFailed;
    public string FilePath { get; private set; }

    public LogFileWriter(string directory, bool pruneFiles)
    {
        _directory = Path.GetFullPath(directory);
        _pruneFiles = pruneFiles;
        Directory.CreateDirectory(_directory);
        FilePath = NextPath();
    }

    // Cleanup failures do not hide the current record or recursively log into this sink.
    public string? Write(string text, DateTimeOffset now)
    {
        var bytes = Encode(text + Environment.NewLine);
        CheckDirectory();
        var current = new FileInfo(FilePath);
        if (current.Attributes != (FileAttributes)(-1) && (current.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("日志文件不能是链接。");
        var day = DateOnly.FromDateTime(now.UtcDateTime);
        var rotated = current.Exists && (current.Length + bytes.Length > MaxFileBytes ||
            (_cleanupDay is { } previousDay && previousDay != day));
        if (rotated) FilePath = NextPath();
        string? warning = null;
        if (rotated || _cleanupDay != day)
        {
            _cleanupDay = day;
            var failure = _pruneFiles ? Prune(now.UtcDateTime) : null;
            if (failure is not null && !_maintenanceFailed) warning = failure;
            _maintenanceFailed = failure is not null;
        }
        using var output = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.Read);
        output.Write(bytes);
        return warning;
    }

    private string NextPath() => Path.Combine(_directory, $"{_session}-{++_part:D4}.log");

    private string? Prune(DateTime utcNow)
    {
        try
        {
            // Top-level, precisely named regular files only; never recurse into user directories.
            var files = new DirectoryInfo(_directory).EnumerateFiles("host-*.log")
                .Where(f => f.FullName != FilePath && IsHostLog(f.Name) &&
                    (f.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) == 0)
                .OrderByDescending(f => f.LastWriteTimeUtc).ThenByDescending(f => f.Name, StringComparer.Ordinal).ToArray();
            // Reserve the full current segment so ordinary appends need no directory scans.
            long keptBytes = MaxFileBytes;
            var keptFiles = 1;
            string? failure = null;
            foreach (var file in files)
            {
                var length = file.Length;
                if (file.LastWriteTimeUtc >= utcNow.AddDays(-RetentionDays) &&
                    keptFiles < MaxFiles && length <= MaxTotalBytes - keptBytes)
                {
                    keptBytes += length;
                    keptFiles++;
                    continue;
                }
                try { file.Delete(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    keptBytes += length;
                    keptFiles++;
                    failure ??= $"{file.Name}：{ex.Message}";
                }
            }
            return failure;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ex.Message; }
    }

    private void CheckDirectory()
    {
        for (var directory = new DirectoryInfo(_directory); directory is not null; directory = directory.Parent)
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("日志目录及其父目录不能是链接；未写入或清理链接位置。");
    }

    private static bool IsHostLog(string name) => HostLogName().IsMatch(name) &&
        DateTime.TryParseExact(name.AsSpan(5, 15), "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    [GeneratedRegex(@"^host-\d{8}-\d{6}-[0-9]+(?:-[a-f0-9]{32}-[0-9]+)?\.log$", RegexOptions.CultureInvariant)]
    private static partial Regex HostLogName();

    private static byte[] Encode(string text)
    {
        if (Encoding.UTF8.GetByteCount(text) <= MaxEntryBytes) return Encoding.UTF8.GetBytes(text);
        var bytes = new byte[MaxEntryBytes];
        // Encoder.Convert stops on a complete Unicode scalar, including emoji surrogate pairs.
        Encoding.UTF8.GetEncoder().Convert(text.AsSpan(), bytes.AsSpan(0, MaxEntryBytes - Truncated.Length),
            true, out _, out var used, out _);
        Truncated.CopyTo(bytes, used);
        Array.Resize(ref bytes, used + Truncated.Length);
        return bytes;
    }
}
