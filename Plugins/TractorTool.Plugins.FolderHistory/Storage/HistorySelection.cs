using TractorTool.Plugins.FolderHistory.Models;

namespace TractorTool.Plugins.FolderHistory.Storage;

internal static class HistorySelection
{
    public const string Root = "";

    public static string[] Normalize(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.Count == 0) throw new InvalidDataException("请明确选择要恢复的文件、文件夹或根目录。");
        var normalized = paths.Select(path => path == Root ? Root : HistoryPaths.NormalizeRelative(path)).ToHashSet(HistoryPaths.Comparer);
        if (normalized.Contains(Root)) return [Root];
        return normalized.Where(path => !Ancestors(path).Any(normalized.Contains)).Order(StringComparer.Ordinal).ToArray();
    }

    public static string[] Resolve(HistoryVersion version, IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(paths);
        if (paths is [Root]) return [Root];
        var entries = version.Entries.ToDictionary(entry => entry.Path, HistoryPaths.Comparer);
        // Validate every explicit item before collapsing overlaps; a missing child must not silently disappear.
        return Normalize(paths.Select(path => path == Root ? Root :
            entries.TryGetValue(HistoryPaths.NormalizeRelative(path), out var entry) ? entry.Path :
                throw new InvalidDataException("选定版本没有所选项目，未执行恢复：" + path)).ToArray());
    }

    public static bool Contains(IReadOnlySet<string> selected, string path) =>
        selected.Contains(Root) || selected.Contains(path) || Ancestors(path).Any(selected.Contains);

    public static IReadOnlyList<HistoryEntry> Scope(IReadOnlyList<HistoryEntry> entries, IReadOnlyList<string> paths)
    {
        var selected = paths.ToHashSet(HistoryPaths.Comparer);
        var ancestors = paths.SelectMany(Ancestors).ToHashSet(HistoryPaths.Comparer);
        return entries.Where(entry => Contains(selected, entry.Path) || entry.IsDirectory && ancestors.Contains(entry.Path))
            .OrderBy(entry => entry.Path, StringComparer.Ordinal).ToArray();
    }

    public static IEnumerable<string> Ancestors(string path)
    {
        for (var separator = path.LastIndexOf('/'); separator >= 0; separator = path.LastIndexOf('/'))
        {
            path = path[..separator];
            yield return path;
        }
    }
}
