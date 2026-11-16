namespace TractorTool.Plugins.FolderHistory.Models;

public sealed record HistoryEntry(string Path, bool IsDirectory, long Length, string Hash);

public sealed record HistoryVersion(string Id, DateTimeOffset CreatedAt, string SourceRoot,
    IReadOnlyList<HistoryEntry> Entries);

public sealed record SavedVersion(HistoryVersion Version, string Note, bool Pinned)
{
    public string Id => Version.Id;
    public string Title => Version.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
}

public sealed record FolderInfo(string Root, Guid Id, string Status, int VersionCount,
    DateTimeOffset? LastSavedAt);

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
