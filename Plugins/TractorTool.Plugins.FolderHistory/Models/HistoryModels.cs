namespace TractorTool.Plugins.FolderHistory.Models;

public sealed record HistoryEntry(string Path, bool IsDirectory, long Length, string Hash);

public sealed class WorkingTreeChangedException(string message, Exception? innerException) : IOException(message, innerException)
{
    public WorkingTreeChangedException(string message) : this(message, null) { }
}

public sealed record HistoryVersion(string Id, DateTimeOffset CreatedAt, string SourceRoot,
    IReadOnlyList<HistoryEntry> Entries);

public sealed record SavedVersion(HistoryVersion Version, string Note, bool Pinned, long Number)
{
    public string Id => Version.Id;
    public string NumberLabel => $"#{Number}";
    public string SavedAt => Version.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    public string Title => $"{NumberLabel} · {SavedAt}";
    public bool IsRestoreProtection { get; init; }
}

public sealed record FolderInfo(string Root, Guid Id, string Status, int VersionCount,
    DateTimeOffset? LastSavedAt)
{
    public bool ProtectionEnabled { get; init; }
    public bool HasPendingRestore { get; init; }
    public bool HasPendingCleanup { get; init; }
    public bool NeedsAttention { get; init; }
    public long HistoryBytes { get; init; }
}

public sealed record FolderSettings
{
    public int StableSeconds { get; init; } = 5;
    public int MinimumIntervalSeconds { get; init; } = 60;
    public int KeepLatestVersions { get; init; }
    public int KeepDays { get; init; }
    public string[] ExcludedPaths { get; init; } = [];
}

public sealed record FolderPreparation(string Root, int FileCount, int DirectoryCount, long TotalBytes,
    string HistoryDirectory, long? AvailableBytes, bool ExistingHistory)
{
    public bool WorkingTreeScanned { get; init; } = true;
}

public sealed record RestorePreview(Guid Id, string Root, string VersionId, string? RelativePath,
    IReadOnlyList<HistoryChange> Changes)
{
    public IReadOnlyList<string> RelativePaths { get; init; } = RelativePath is null ? [""] : [RelativePath];
}

public sealed record PendingRestoreInfo(string TargetVersionId, string ProtectionVersionId, string Status);

public sealed record CleanupPreview(Guid Id, string Root, bool DeleteAll, IReadOnlyList<SavedVersion> Versions);

public sealed record HistoryChange(string Path, string Kind, long? BeforeLength, long? AfterLength);

public static class HistoryComparison
{
    public static IReadOnlyList<HistoryChange> Compare(IReadOnlyList<HistoryEntry> before,
        IReadOnlyList<HistoryEntry> after)
    {
        var previous = before.ToDictionary(item => item.Path, StringComparer.Ordinal);
        var current = after.ToDictionary(item => item.Path, StringComparer.Ordinal);
        return previous.Keys.Union(current.Keys, StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Where(path => !previous.TryGetValue(path, out var old) || !current.TryGetValue(path, out var next) || old != next)
            .Select(path => new HistoryChange(path,
                !previous.ContainsKey(path) ? "新增" : !current.ContainsKey(path) ? "删除" : "修改",
                previous.TryGetValue(path, out var old) && !old.IsDirectory ? old.Length : null,
                current.TryGetValue(path, out var next) && !next.IsDirectory ? next.Length : null)).ToArray();
    }

    public static bool Equal(IReadOnlyList<HistoryEntry> first, IReadOnlyList<HistoryEntry> second) =>
        Compare(first, second).Count == 0;
}
