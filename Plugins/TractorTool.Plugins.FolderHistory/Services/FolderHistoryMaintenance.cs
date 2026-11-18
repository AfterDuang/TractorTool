using TractorTool.Plugins.FolderHistory.Models;
using TractorTool.Contracts;
using TractorTool.Plugins.FolderHistory.Processing;
using TractorTool.Plugins.FolderHistory.Storage;

namespace TractorTool.Plugins.FolderHistory.Services;

public static class HistoryRetention
{
    public static IReadOnlyList<SavedVersion> Candidates(IReadOnlyList<SavedVersion> versions, FolderSettings settings, DateTimeOffset now)
    {
        if (settings.KeepLatestVersions == 0 && settings.KeepDays == 0) return [];
        return versions.OrderByDescending(version => version.Number).Select((version, index) => (version, index))
            .Where(item => item.index > 0 && !item.version.Pinned && !item.version.IsRestoreProtection &&
                (settings.KeepLatestVersions == 0 || item.index >= settings.KeepLatestVersions) &&
                (settings.KeepDays == 0 || item.version.Version.CreatedAt < now.AddDays(-settings.KeepDays)))
            .Select(item => item.version).ToArray();
    }
}

public sealed partial class FolderHistoryService
{
    private readonly Dictionary<string, FolderSettings> _cleanupRules = new(HistoryPaths.Comparer);
    public Task<IReadOnlyList<SavedVersion>> GetCleanupPreviewAsync(string root, bool deleteAll = false) => ExclusiveAsync(token =>
    {
        _ = Open(root);
        using var ownership = HistoryStore.Lock(root);
        var state = Open(root);
        RequireNoPendingOperation(root, state);
        var versions = ReadCommitted(root, state);
        if (deleteAll && versions.Any(version => version.Pinned))
            throw new InvalidOperationException("存在重要版本，请先逐个取消重要标记，再确认删除全部版本。");
        if (!deleteAll) _cleanupRules[root] = state.Settings with { ExcludedPaths = [.. state.Settings.ExcludedPaths] };
        return Task.FromResult(deleteAll ? versions : HistoryRetention.Candidates(versions, state.Settings, DateTimeOffset.UtcNow));
    });

    public Task DeleteVersionsAsync(string root, IReadOnlyList<string> ids, bool deleteAll = false)
    {
        var selected = ids.ToArray();
        return ExclusiveAsync(async token =>
        {
            _ = Open(root);
            using var lease = await RequestEngineAsync(token).ConfigureAwait(false);
            using var ownership = HistoryStore.Lock(root);
            var state = Open(root);
            RequireNoPendingOperation(root, state);
            var versions = ReadCommitted(root, state);
            if (selected.Length == 0 || selected.Distinct(StringComparer.Ordinal).Count() != selected.Length ||
                selected.Any(id => versions.All(version => version.Id != id)))
                throw new InvalidOperationException("待删除版本已变化，请重新预览。");
            if (versions.Any(version => selected.Contains(version.Id) && version.Pinned))
                throw new InvalidOperationException("待删除版本中存在重要版本；未删除任何版本，请先取消重要标记。");
            if (deleteAll && selected.Length != versions.Count || !deleteAll && selected.Length == versions.Count)
                throw new InvalidOperationException("全部版本的删除范围已变化或尚未明确确认，请重新预览；未扩大删除范围。");
            if (!deleteAll && _cleanupRules.TryGetValue(root, out var previewRules))
            {
                var candidates = HistoryRetention.Candidates(versions, state.Settings, DateTimeOffset.UtcNow).Select(version => version.Id).ToHashSet(StringComparer.Ordinal);
                if (previewRules.KeepDays != state.Settings.KeepDays || previewRules.KeepLatestVersions != state.Settings.KeepLatestVersions ||
                    selected.Any(id => !candidates.Contains(id)))
                    throw new InvalidOperationException("保留规则或版本保护状态已变化，请重新预览清理范围。");
            }
            await DeleteCoreAsync(root, state, Engine(lease), selected, token, deleteAll: deleteAll).ConfigureAwait(false);
            _cleanupRules.Remove(root);
            ConfigureObservation(root, state, recheck: true);
            NotifyChanged();
            return true;
        });
    }

    public Task ResumeCleanupAsync(string root) => ExclusiveAsync(async token =>
    {
        _ = Open(root);
        using var lease = await RequestEngineAsync(token).ConfigureAwait(false);
        using var ownership = HistoryStore.Lock(root);
        var state = Open(root);
        if (InPlaceRestoreTransaction.ReadPending(root) is not null) throw new InvalidOperationException("请先处理未完成恢复。");
        if (state.PendingDeleteIds.Count == 0) throw new InvalidOperationException("没有待继续的清理。");
        if (state.Versions.Any(version => state.PendingDeleteIds.Contains(version.Id) && version.Pinned))
            throw new InvalidOperationException("待清理版本已被标记为重要，请先核对并取消标记，再继续清理。");
        await DeleteCoreAsync(root, state, Engine(lease), state.PendingDeleteIds.ToArray(), token, resume: true, deleteAll: state.PendingDeleteAll).ConfigureAwait(false);
        ConfigureObservation(root, state, recheck: true);
        NotifyChanged();
        return true;
    });

    private Task ApplyRetentionAsync(string root, RepositoryState state, DuplicatiEngine engine, CancellationToken token)
    {
        if (state.Settings.KeepLatestVersions == 0 && state.Settings.KeepDays == 0) return Task.CompletedTask;
        var selected = HistoryRetention.Candidates(ReadCommitted(root, state), state.Settings, DateTimeOffset.UtcNow);
        return selected.Count == 0 ? Task.CompletedTask : DeleteCoreAsync(root, state, engine, selected.Select(version => version.Id).ToArray(), token);
    }

    private async Task DeleteCoreAsync(string root, RepositoryState state, DuplicatiEngine engine, string[] selected, CancellationToken token,
        bool resume = false, bool deleteAll = false)
    {
        if (!resume) DuplicatiEngine.PreflightDelete(HistoryPaths.HistoryDirectory(root), selected, deleteAll);
        var deletionStarted = resume;
        state.PendingDeleteIds = selected.ToList();
        state.PendingDeleteAll = deleteAll;
        if (state.PendingDeleteAll) state.ProtectionEnabled = false;
        HistoryStore.Save(root, state);
        SetRuntimeStatus(root, "正在清理历史");
        try
        {
            await RunTaskAsync("清理历史版本 · " + Path.GetFileName(root), async (progress, cancellation) =>
            {
                progress.Report(new(null, "正在由历史引擎删除选定版本并回收内容"));
                await engine.DeleteVersionsAsync(HistoryPaths.HistoryDirectory(root), selected, cancellation, resume, state.PendingDeleteAll,
                    deletionStarting: () => deletionStarted = true).ConfigureAwait(false);
                state.Versions.RemoveAll(version => selected.Contains(version.Id));
                state.PendingDeleteIds.Clear();
                state.PendingDeleteAll = false;
                HistoryStore.Save(root, state);
                return "已删除选定的 " + selected.Length + " 个版本；当前工作文件未改动。";
            }, token).ConfigureAwait(false);
            RestoreObservationAfterMaintenance(root);
        }
        catch
        {
            var message = "历史清理未完成，请检查后继续";
            if (!deletionStarted)
            {
                state.PendingDeleteIds.Clear();
                state.PendingDeleteAll = false;
                try
                {
                    HistoryStore.Save(root, state);
                    message = "清理准备失败，尚未删除版本；请检查后重试";
                }
                catch (Exception error)
                { _context.Log.Write(AppLogLevel.Warning, _context.PluginId, "尚未开始删除，但无法清除清理准备记录。", error); }
            }
            SetRuntimeStatus(root, message, faulted: true);
            throw;
        }
    }
}
