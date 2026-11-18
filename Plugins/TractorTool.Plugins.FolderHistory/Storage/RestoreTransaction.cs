using TractorTool.Plugins.FolderHistory.Models;

namespace TractorTool.Plugins.FolderHistory.Storage;

public static class RestoreTransaction
{
    public static Task SaveAsAsync(string staging, string destination, string? selection,
        IReadOnlyList<HistoryEntry> expected, CancellationToken token) =>
        SaveAsCoreAsync(staging, destination, selection, expected, singleFile: false, token);

    public static Task SaveFileAsAsync(string staging, string destination, string selection,
        IReadOnlyList<HistoryEntry> expected, CancellationToken token) =>
        SaveAsCoreAsync(staging, destination, selection, expected, singleFile: true, token);

    private static async Task SaveAsCoreAsync(string staging, string destination, string? selection,
        IReadOnlyList<HistoryEntry> expected, bool singleFile, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        staging = HistoryPaths.Absolute(staging);
        destination = HistoryPaths.Absolute(destination);
        HistoryPaths.RejectHistoryContainer(destination);
        HistoryPaths.RequireLocalVolume(destination);
        if (HistoryPaths.Overlaps(staging, destination)) throw new InvalidDataException("恢复源与另存目录不能重叠。");
        var parent = Path.GetDirectoryName(destination);
        if (parent is null) throw new InvalidDataException("请选择专用的另存子目录。");
        HistoryPaths.RequireDirectory(parent);
        if (HistoryPaths.Attributes(destination) is not null) throw new IOException("另存位置已存在，不会覆盖：" + destination);

        var entries = expected.OrderBy(item => item.Path, StringComparer.Ordinal).ToArray();
        var sourceContents = await WorkingTree.ScanAsync(staging, excludeHistory: false, token).ConfigureAwait(false);
        if (!entries.SequenceEqual(sourceContents)) throw new IOException("提取内容与选定版本不符，未写入另存位置。");
        HistoryEntry? selected = null;
        if (selection is not null || singleFile)
        {
            var selectedPath = HistoryPaths.NormalizeRelative(selection!);
            selected = entries.SingleOrDefault(item => HistoryPaths.Comparer.Equals(item.Path, selectedPath))
                ?? throw new InvalidDataException("历史版本没有所选项目：" + selectedPath);
            if (singleFile && selected.IsDirectory) throw new InvalidDataException("不能把历史目录另存为文件。");
            entries = HistorySelection.Scope(entries, [selected.Path]).ToArray();
        }

        var temporary = Path.Combine(parent, ".tractortool-restore-" + Guid.NewGuid().ToString("N"));
        if (HistoryPaths.Attributes(temporary) is not null) throw new IOException("另存临时目录已被占用。");
        token.ThrowIfCancellationRequested();
        var created = new List<(string Path, bool IsDirectory)>();
        try
        {
            Directory.CreateDirectory(temporary);
            created.Add((temporary, true));
            foreach (var entry in entries)
            {
                token.ThrowIfCancellationRequested();
                var target = HistoryPaths.ResolveWorkPath(temporary, entry.Path);
                if (entry.IsDirectory)
                {
                    if (HistoryPaths.Attributes(target) is not null) throw new IOException("暂存目录已被占用：" + entry.Path);
                    Directory.CreateDirectory(target);
                    created.Add((target, true));
                }
                else
                {
                    var source = HistoryPaths.ResolveWorkPath(staging, entry.Path);
                    await WorkingTree.CopyFileAsync(source, target, () => created.Add((target, false)), token).ConfigureAwait(false);
                }
            }
            var verified = await WorkingTree.ScanAsync(temporary, excludeHistory: false, token).ConfigureAwait(false);
            if (!entries.SequenceEqual(verified)) throw new IOException("另存暂存区内容与选定版本不符，未发布恢复结果。");
            HistoryPaths.RequireDirectory(parent);
            HistoryPaths.RequireDirectory(temporary);
            HistoryPaths.CheckPath(destination);
            token.ThrowIfCancellationRequested();
            if (singleFile)
            {
                var source = HistoryPaths.ResolveWorkPath(temporary, selected!.Path);
                created.Remove((source, false));
                await WorkingTree.MoveExpectedFileAsync(source, destination, selected, token).ConfigureAwait(false);
            }
            else
            {
                Directory.Move(temporary, destination);
                created.Clear();
            }
        }
        finally
        {
            // Leave unfamiliar paths intact, even when a user has entered our temporary directory.
            foreach (var item in created.AsEnumerable().Reverse())
            {
                try
                {
                    HistoryPaths.CheckPath(item.Path);
                    if (item.IsDirectory) Directory.Delete(item.Path, recursive: false);
                    else File.Delete(item.Path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { }
            }
        }
    }
}
