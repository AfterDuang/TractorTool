using TractorTool.Contracts;
using TractorTool.Plugins.FolderHistory.Models;
using TractorTool.Plugins.FolderHistory.Processing;
using TractorTool.Plugins.FolderHistory.Storage;

namespace TractorTool.Plugins.FolderHistory.Services;

public sealed partial class FolderHistoryService
{
    public Task<bool> PublishSelectionAsync(string root, string versionId, IReadOnlyList<string> relativePaths, string note)
    {
        var paths = relativePaths.ToArray();
        note = NormalizeNote(note);
        return ExclusiveAsync(async token =>
        {
            _ = Open(root);
            using var ownership = HistoryStore.Lock(root);
            var state = Open(root);
            RequireNoPendingOperation(root, state);
            var historical = ReadCommittedVersion(root, state, versionId);
            var latest = ReadCommittedVersion(root, state.Versions.Last());
            var selections = HistorySelection.Resolve(historical, paths);
            var selected = HistorySelection.Scope(historical.Entries, selections);
            var selectedPaths = selections.ToHashSet(HistoryPaths.Comparer);
            var merged = latest.Entries.Where(entry => !HistorySelection.Contains(selectedPaths, entry.Path))
                .ToDictionary(entry => entry.Path, HistoryPaths.Comparer);
            foreach (var entry in selected)
            {
                if (merged.TryGetValue(entry.Path, out var parent) && parent != entry)
                    throw new InvalidOperationException("所选路径的上级与最新快照不同，请选择该上级后重新发布：" + entry.Path);
                merged[entry.Path] = entry;
            }
            var expected = merged.Values.OrderBy(entry => entry.Path, StringComparer.Ordinal).ToArray();
            if (HistoryComparison.Equal(expected, latest.Entries)) return false;
            if (state.LastSnapshotNumber == long.MaxValue) throw new InvalidDataException("历史库的快照编号已用尽。");
            var selectedFiles = selected.Where(entry => !entry.IsDirectory).Select(entry => entry.Path).ToHashSet(StringComparer.Ordinal);
            var latestFiles = expected.Where(entry => !entry.IsDirectory && !selectedFiles.Contains(entry.Path)).Select(entry => entry.Path).ToArray();
            IReadOnlyList<HistoryEntry> latestExtraction = latestFiles.Length == 0 ? [] : DuplicatiEngine.SelectEntriesForSelection(latest, latestFiles);

            var history = HistoryPaths.HistoryDirectory(root);
            var source = Path.Combine(history, "temp", "publish-source-" + Guid.NewGuid().ToString("N"));
            HistoryPaths.CheckPath(source);
            if (HistoryPaths.Attributes(source) is not null) throw new IOException("发布暂存目录已被占用。");
            var latestStage = Path.Combine(history, "temp", "publish-latest-" + Guid.NewGuid().ToString("N"));
            RequireSpace(root, checked(latestExtraction.Sum(entry => entry.Length) + selected.Sum(entry => entry.Length)), "提取待发布的历史内容");
            var movedLatest = new HashSet<string>(StringComparer.Ordinal);
            using var lease = await RequestEngineAsync(token).ConfigureAwait(false);
            var engine = Engine(lease);
            state.ProtectionEnabled = false;
            HistoryStore.Save(root, state);
            ConfigureObservation(root, state, recheck: false);
            try
            {
                await RunTaskAsync("将历史选区发布为新快照 · " + Path.GetFileName(root), async (progress, cancellation) =>
                {
                    SetRuntimeStatus(root, "正在发布历史选区；自动保护已暂停");
                    progress.Report(new(null, "正在提取并校验历史内容"));
                    if (latestFiles.Length > 0)
                        await engine.RestoreSelectionAsync(latest, history, latestStage, cancellation, latestFiles).ConfigureAwait(false);
                    await engine.RestoreSelectionAsync(historical, history, source, cancellation, selections).ConfigureAwait(false);
                    foreach (var entry in expected)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        var destination = HistoryPaths.ResolveWorkPath(source, entry.Path);
                        if (entry.IsDirectory) Directory.CreateDirectory(destination);
                        else if (!selectedFiles.Contains(entry.Path))
                        {
                            var input = HistoryPaths.ResolveWorkPath(latestStage, entry.Path);
                            await WorkingTree.MoveExpectedFileAsync(input, destination, entry, cancellation).ConfigureAwait(false);
                            movedLatest.Add(entry.Path);
                        }
                    }
                    if (!HistoryComparison.Equal(expected, await WorkingTree.ScanAsync(source, excludeHistory: false, cancellation).ConfigureAwait(false)))
                        throw new IOException("发布暂存内容与确认的历史选区不符，未创建快照。");
                    progress.Report(new(null, "正在保存并校验新快照"));
                    RequireSpace(root, expected.Sum(entry => entry.Length), "保存发布快照");
                    var created = await engine.BackupStagedAsync(source, history, cancellation).ConfigureAwait(false);
                    if (!HistoryComparison.Equal(expected, created.Entries) ||
                        !HistoryComparison.Equal(expected, await WorkingTree.ScanAsync(source, excludeHistory: false, cancellation).ConfigureAwait(false)))
                        throw new IOException("引擎结果与待发布内容不符，本次未登记为可用快照。");
                    cancellation.ThrowIfCancellationRequested();
                    state.AddVersion(created.Id, created.SourceRoot, note, false);
                    HistoryStore.Save(root, state);
                    return "已将历史选区发布为最新快照；当前工作文件未改动，自动保护已暂停。";
                }, token).ConfigureAwait(false);
                SetRuntimeStatus(root, "历史选区已发布；自动保护已暂停");
                return true;
            }
            catch
            {
                SetRuntimeStatus(root, "历史选区发布未完成；自动保护已暂停", faulted: true);
                throw;
            }
            finally
            {
                await CleanupPublishedStagingAsync(latestStage, latestExtraction.Where(entry => !movedLatest.Contains(entry.Path)).ToArray()).ConfigureAwait(false);
                await CleanupPublishedStagingAsync(source, expected).ConfigureAwait(false);
                NotifyChanged();
            }
        });
    }

    private async Task CleanupPublishedStagingAsync(string path, IReadOnlyList<HistoryEntry> expected)
    {
        try
        {
            HistoryPaths.CheckPath(path);
            if (HistoryPaths.Attributes(path) is null) return;
            var actual = await WorkingTree.ScanAsync(path, excludeHistory: false, CancellationToken.None).ConfigureAwait(false);
            if (!HistoryComparison.Equal(expected, actual)) throw new IOException("暂存内容不完整或已有变化，已保留：" + path);
            CleanupStaging(path, expected);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        { _context.Log.Write(AppLogLevel.Warning, _context.PluginId, "发布暂存未能清理：" + path, error); }
    }
}
