namespace TractorTool.Plugins.FolderHistory.Storage;

public static class HistoryExclusions
{
    public static IReadOnlyList<string> Normalize(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var result = new List<string>();
        foreach (var path in paths.Select(path => HistoryPaths.NormalizeRelative(path.TrimEnd('/', '\\')))
                     .Distinct(HistoryPaths.Comparer).OrderBy(path => path.Length).ThenBy(path => path, StringComparer.Ordinal))
            if (!IsExcluded(path, result)) result.Add(path);
        return result.OrderBy(path => path, StringComparer.Ordinal).ToArray();
    }

    public static bool IsExcluded(string path, IReadOnlyList<string> rules) =>
        HistoryPaths.IsHistoryPath(path) || rules.Any(rule => HistoryPaths.IsSameOrDescendant(rule, path));
}
