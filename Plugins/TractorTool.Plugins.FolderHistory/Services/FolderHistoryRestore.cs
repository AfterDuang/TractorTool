using System.Security.Cryptography;
using TractorTool.Contracts;
using TractorTool.Plugins.FolderHistory.Models;
using TractorTool.Plugins.FolderHistory.Processing;
using TractorTool.Plugins.FolderHistory.Storage;

namespace TractorTool.Plugins.FolderHistory.Services;

public sealed partial class FolderHistoryService
{
    private readonly Dictionary<Guid, (RestorePreview Preview, InPlaceRestorePlan Plan)> _restorePreviews = [];

    public Task<RestorePreview> PreviewRestoreAsync(string root, string versionId, string? relativePath = null) => ExclusiveAsync(async token =>
    {
        _ = Open(root);
        using var ownership = HistoryStore.Lock(root);
        var state = Open(root);
        RequireNoPendingOperation(root, state);
        var saved = ReadCommitted(root, state).SingleOrDefault(item => item.Id == versionId)
            ?? throw new InvalidOperationException("选定版本不可用，请刷新历史列表。");
        var plan = await InPlaceRestoreTransaction.PreviewAsync(root, saved.Version, relativePath,
            state.Settings.ExcludedPaths.ToArray(), token).ConfigureAwait(false);
        var preview = new RestorePreview(Guid.NewGuid(), root, versionId, relativePath, plan.Changes.ToArray());
        foreach (var id in _restorePreviews.Where(pair => HistoryPaths.Comparer.Equals(pair.Value.Preview.Root, root)).Select(pair => pair.Key).ToArray())
            _restorePreviews.Remove(id);
        _restorePreviews[preview.Id] = (preview, plan);
        return preview;
    });

    public Task RestoreInPlaceAsync(RestorePreview preview) => ExclusiveAsync(async token =>
    {
        if (!_restorePreviews.TryGetValue(preview.Id, out var prepared) || prepared.Preview != preview)
            throw new InvalidOperationException("恢复预览已失效，请重新预览。");
        var root = prepared.Preview.Root;
        var plan = prepared.Plan;
        _ = Open(root);
        using var lease = await RequestEngineAsync(token).ConfigureAwait(false);
        using var ownership = HistoryStore.Lock(root);
        var state = Open(root);
        RequireNoPendingOperation(root, state);
        if (!state.Settings.ExcludedPaths.SequenceEqual(plan.Exclusions, HistoryPaths.Comparer))
            throw new InvalidOperationException("排除规则已变化，请重新预览恢复范围。");
        var target = ReadCommitted(root, state).SingleOrDefault(version => version.Id == prepared.Preview.VersionId)
            ?? throw new InvalidOperationException("目标版本已不可用，请重新预览。");
        await InPlaceRestoreTransaction.EnsurePreviewCurrentAsync(root, plan, token).ConfigureAwait(false);
        var engine = Engine(lease);
        state.ProtectionEnabled = false;
        HistoryStore.Save(root, state);
        ConfigureObservation(root, state, recheck: false);
        var protection = await CaptureAsync(root, state, engine, "恢复前完整保护", true, token).ConfigureAwait(false);
        foreach (var entry in plan.Baseline)
            if (!protection.Version.Entries.Contains(entry))
                throw new IOException("恢复前版本未包含预览中的当前内容，未覆盖工作文件。请重新预览。");
        await InPlaceRestoreTransaction.EnsurePreviewCurrentAsync(root, plan, token).ConfigureAwait(false);
        var history = HistoryPaths.HistoryDirectory(root);
        var staging = Path.Combine(history, "temp", "in-place-" + Guid.NewGuid().ToString("N"));
        var selection = plan.Selection;
        var selectionExists = selection is null || target.Version.Entries.Any(entry => HistoryPaths.Comparer.Equals(entry.Path, selection));
        IReadOnlyList<HistoryEntry> extractedEntries = selectionExists ? DuplicatiEngine.SelectEntries(target.Version, selection) : [];
        var targetBytes = checked(extractedEntries.Sum(entry => entry.Length) * 2);
        RequireSpace(root, Math.Max(protection.Version.Entries.Sum(entry => entry.Length), targetBytes), "验证恢复前保护并准备暂存");
        try
        {
            await RunTaskAsync("恢复到原位置 · " + Path.GetFileName(root), async (progress, cancellation) =>
            {
                SetRuntimeStatus(root, "正在恢复；自动保护已暂停");
                progress.Report(new(null, "正在实际提取并校验恢复前完整版本"));
                await VerifyProtectionAsync(root, protection.Version, engine, cancellation).ConfigureAwait(false);
                await InPlaceRestoreTransaction.EnsurePreviewCurrentAsync(root, plan, cancellation).ConfigureAwait(false);
                RequireSpace(root, targetBytes, "准备恢复目标与事务暂存");
                progress.Report(new(null, "正在提取并校验恢复内容"));
                if (selectionExists)
                    await engine.RestoreAsync(target.Version, history, staging, cancellation, selection).ConfigureAwait(false);
                else
                {
                    HistoryPaths.CheckPath(staging);
                    if (HistoryPaths.Attributes(staging) is not null) throw new IOException("恢复暂存目录已被占用。");
                    Directory.CreateDirectory(staging);
                }
                await InPlaceRestoreTransaction.EnsurePreviewCurrentAsync(root, plan, cancellation).ConfigureAwait(false);
                progress.Report(new(null, "正在按已确认的范围恢复工作文件"));
                var warning = await InPlaceRestoreTransaction.ApplyAsync(root, plan, staging, protection.Id, cancellation).ConfigureAwait(false);
                if (warning is not null) _context.Log.Write(AppLogLevel.Warning, _context.PluginId, warning);
                return "恢复完成，恢复前完整版本已保留；可重新开启自动保护。";
            }, token).ConfigureAwait(false);
            _restorePreviews.Remove(preview.Id);
            CleanupStaging(staging, extractedEntries);
            SetRuntimeStatus(root, "已恢复，自动保护已暂停");
        }
        catch
        {
            SetRuntimeStatus(root, InPlaceRestoreTransaction.ReadPending(root) is null
                ? "恢复准备未完成，当前工作文件未进入恢复；自动保护已暂停"
                : "恢复未完成，请继续恢复或撤销本次恢复", faulted: true);
            throw;
        }
        NotifyChanged();
        return true;
    });

    public Task<PendingRestoreInfo?> GetPendingRestoreAsync(string root) => ExclusiveAsync(token =>
    {
        _ = Open(root);
        using var ownership = HistoryStore.Lock(root);
        var pending = InPlaceRestoreTransaction.ReadPending(root);
        return Task.FromResult(pending is null ? null : new PendingRestoreInfo(pending.Plan.TargetVersionId, pending.ProtectionVersionId, pending.Status));
    });

    public Task ResumeRestoreAsync(string root) => FinishRestoreAsync(root, rollback: false);
    public Task AbandonRestoreAsync(string root) => FinishRestoreAsync(root, rollback: true);

    private Task FinishRestoreAsync(string root, bool rollback) => ExclusiveAsync(async token =>
    {
        _ = Open(root);
        using var ownership = HistoryStore.Lock(root);
        var state = Open(root);
        var pending = InPlaceRestoreTransaction.ReadPending(root) ?? throw new InvalidOperationException("没有未完成恢复。");
        var receipt = state.Versions.SingleOrDefault(version => version.Id == pending.ProtectionVersionId);
        if (receipt is not { RestoreProtection: true, Pinned: true })
            throw new InvalidDataException("恢复前保护版本缺失或标记损坏，已停止自动处理，请保留历史库。");
        HistoryVersion? protection = null;
        if (!rollback)
        {
            protection = DuplicatiEngine.ReadVersion(HistoryPaths.HistoryDirectory(root), receipt.Id);
            if (protection is null || !HistoryPaths.Comparer.Equals(protection.SourceRoot, receipt.SourceRoot) ||
                pending.Plan.Baseline.Any(entry => !protection.Entries.Contains(entry)))
                throw new InvalidDataException("本次恢复前保护版本无法核对，已保留恢复材料。");
        }
        using var lease = rollback ? null : await RequestEngineAsync(token).ConfigureAwait(false);
        state.ProtectionEnabled = false;
        HistoryStore.Save(root, state);
        ConfigureObservation(root, state, recheck: false);
        await RunTaskAsync((rollback ? "撤销未完成恢复 · " : "继续未完成恢复 · ") + Path.GetFileName(root), async (progress, cancellation) =>
        {
            if (!rollback)
            {
                progress.Report(new(null, "正在重新提取并校验恢复前完整版本"));
                await VerifyProtectionAsync(root, protection!, Engine(lease!), cancellation).ConfigureAwait(false);
            }
            progress.Report(new(null, "正在核对工作文件与恢复记录"));
            var warning = rollback ? await InPlaceRestoreTransaction.RollbackAsync(root, cancellation).ConfigureAwait(false)
                : await InPlaceRestoreTransaction.ResumeAsync(root, cancellation).ConfigureAwait(false);
            if (warning is not null) _context.Log.Write(AppLogLevel.Warning, _context.PluginId, warning);
            return rollback ? pending.Status == "Preparing" ? "已取消尚未执行的恢复，当前工作文件未改动；自动保护仍暂停。"
                : "已撤销本次恢复并校验原状态，自动保护仍暂停。" : "已完成恢复并校验结果，自动保护仍暂停。";
        }, token).ConfigureAwait(false);
        ConfigureObservation(root, state, recheck: false);
        SetRuntimeStatus(root, rollback ? "恢复已撤销，自动保护已暂停" : "已恢复，自动保护已暂停");
        NotifyChanged();
        return true;
    });

    private async Task VerifyProtectionAsync(string root, HistoryVersion protection, DuplicatiEngine engine, CancellationToken token)
    {
        RequireSpace(root, protection.Entries.Sum(entry => entry.Length), "提取并校验恢复前保护");
        var history = HistoryPaths.HistoryDirectory(root);
        var staging = Path.Combine(history, "temp", "verify-protection-" + Guid.NewGuid().ToString("N"));
        await engine.RestoreAsync(protection, history, staging, token).ConfigureAwait(false);
        CleanupStaging(staging, protection.Entries);
    }

    public Task<string> ExportAsync(string root, string versionId, string parentDirectory, string? relativePath = null) =>
        ExclusiveAsync(async token =>
        {
            _ = Open(root);
            var parent = HistoryPaths.Absolute(parentDirectory);
            HistoryPaths.RejectHistoryContainer(parent);
            HistoryPaths.RequireLocalVolume(parent);
            HistoryPaths.RequireDirectory(parent);
            using var lease = await RequestEngineAsync(token).ConfigureAwait(false);
            using var ownership = HistoryStore.Lock(root);
            var state = Open(root);
            var saved = ReadCommitted(root, state).SingleOrDefault(item => item.Id == versionId)
                ?? throw new InvalidOperationException("选定版本不可用，请刷新历史列表。");
            var selected = DuplicatiEngine.SelectEntries(saved.Version, relativePath);
            var history = HistoryPaths.HistoryDirectory(root);
            var staging = Path.Combine(history, "temp", "restore-" + Guid.NewGuid().ToString("N"));
            var destination = Path.Combine(parent, Path.GetFileName(root) + "-" + saved.Version.CreatedAt.ToLocalTime().ToString("yyyyMMdd-HHmmss") +
                "-" + Guid.NewGuid().ToString("N")[..8]);
            if (_registry.Folders.Any(folder => HistoryPaths.Overlaps(destination, folder.Root)) ||
                HistoryPaths.Overlaps(destination, _context.Paths.AppRoot) || HistoryPaths.Overlaps(destination, _context.Paths.DataRoot))
                throw new InvalidOperationException("另存位置应与受管理目录、软件目录和应用数据目录互不包含。");
            RequireSpace(root, selected.Sum(entry => entry.Length), "提取历史内容");
            RequireSpace(parent, selected.Sum(entry => entry.Length), "另存历史内容");
            await RunTaskAsync("另存历史版本 · " + Path.GetFileName(root), async (progress, cancellation) =>
            {
                progress.Report(new(null, "正在提取并校验历史内容"));
                await Engine(lease).RestoreAsync(saved.Version, history, staging, cancellation, relativePath).ConfigureAwait(false);
                progress.Report(new(null, "正在另存到新目录"));
                await RestoreTransaction.SaveAsAsync(staging, destination, relativePath, selected, cancellation).ConfigureAwait(false);
                return destination;
            }, token).ConfigureAwait(false);
            CleanupStaging(staging, selected);
            NotifyChanged();
            return destination;
        });

    public Task<byte[]> ReadFileAsync(string root, string versionId, string relativePath, int maxBytes = 256 * 1024) => ExclusiveAsync(async token =>
    {
        if (maxBytes is < 1 or > 256 * 1024) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        relativePath = HistoryPaths.NormalizeRelative(relativePath);
        _ = Open(root);
        using var ownership = HistoryStore.Lock(root);
        var state = Open(root);
        var saved = ReadCommitted(root, state).SingleOrDefault(item => item.Id == versionId)
            ?? throw new InvalidOperationException("选定版本不可用，请刷新历史列表。");
        var entry = saved.Version.Entries.SingleOrDefault(item => HistoryPaths.Comparer.Equals(item.Path, relativePath))
            ?? throw new InvalidOperationException("这个版本没有所选文件。");
        if (entry.IsDirectory) throw new InvalidOperationException("请选择一个文本文件。");
        if (entry.Length > maxBytes) throw new InvalidOperationException($"文件超过文本预览上限 {maxBytes / 1024} KiB，可另存查看。");
        using var lease = await RequestEngineAsync(token).ConfigureAwait(false);
        _ = Open(root);
        var staging = Path.Combine(HistoryPaths.HistoryDirectory(root), "temp", "preview-" + Guid.NewGuid().ToString("N"));
        byte[]? result = null;
        await RunTaskAsync("读取历史文本 · " + Path.GetFileName(relativePath), async (progress, cancellation) =>
        {
            progress.Report(new(null, "正在提取并校验所选文件"));
            await Engine(lease).RestoreAsync(saved.Version, HistoryPaths.HistoryDirectory(root), staging, cancellation, relativePath).ConfigureAwait(false);
            var file = HistoryPaths.ResolveWorkPath(staging, relativePath);
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
            if (stream.Length != entry.Length || stream.Length > maxBytes) throw new IOException("提取文件大小变化，无法预览。");
            result = new byte[(int)stream.Length];
            await stream.ReadExactlyAsync(result, cancellation).ConfigureAwait(false);
            if (Convert.ToHexString(SHA256.HashData(result)) != entry.Hash) throw new IOException("提取文件内容变化，无法预览。");
            return "已读取所选文件。";
        }, token).ConfigureAwait(false);
        CleanupStaging(staging, DuplicatiEngine.SelectEntries(saved.Version, relativePath));
        return result ?? throw new IOException("未读取到所选文件。");
    });
}
