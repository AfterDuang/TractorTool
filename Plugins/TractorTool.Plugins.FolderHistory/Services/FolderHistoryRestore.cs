using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using TractorTool.Contracts;
using TractorTool.Plugins.FolderHistory.Models;
using TractorTool.Plugins.FolderHistory.Processing;
using TractorTool.Plugins.FolderHistory.Storage;

namespace TractorTool.Plugins.FolderHistory.Services;

public sealed partial class FolderHistoryService
{
    // Closing a preview releases its full file manifest; only its ID is needed to reject superseded confirmations.
    private readonly ConditionalWeakTable<RestorePreview, InPlaceRestorePlan> _restorePlans = new();
    private readonly Dictionary<string, Guid> _latestRestorePreview = new(HistoryPaths.Comparer);

    public Task<RestorePreview> PreviewRestoreAsync(string root, string versionId, string? relativePath = null) =>
        PreviewRestoreSelectionAsync(root, versionId, [relativePath ?? HistorySelection.Root]);

    public Task<RestorePreview> PreviewRestoreSelectionAsync(string root, string versionId, IReadOnlyList<string> relativePaths)
    {
        var paths = relativePaths.ToArray();
        return ExclusiveAsync(async token =>
        {
            _ = Open(root);
            using var ownership = HistoryStore.Lock(root);
            var state = Open(root);
            RequireNoPendingOperation(root, state);
            var saved = ReadCommittedVersion(root, state, versionId);
            var plan = await InPlaceRestoreTransaction.PreviewSelectionAsync(root, saved, paths,
                state.Settings.ExcludedPaths.ToArray(), token).ConfigureAwait(false);
            var preview = new RestorePreview(Guid.NewGuid(), plan.Root, versionId, plan.Selection, plan.Changes)
                { RelativePaths = Array.AsReadOnly(plan.SelectedPaths.ToArray()) };
            _restorePlans.Add(preview, plan);
            _latestRestorePreview[preview.Root] = preview.Id;
            return preview;
        });
    }

    public Task<string?> RestoreInPlaceAsync(RestorePreview preview) => ExclusiveAsync(async token =>
    {
        if (!_restorePlans.TryGetValue(preview, out var plan) || _latestRestorePreview.GetValueOrDefault(preview.Root) != preview.Id)
            throw new InvalidOperationException("恢复预览已失效，请重新预览。");
        var root = preview.Root;
        _ = Open(root);
        using var lease = await RequestEngineAsync(token).ConfigureAwait(false);
        using var ownership = HistoryStore.Lock(root);
        var state = Open(root);
        RequireNoPendingOperation(root, state);
        if (!state.Settings.ExcludedPaths.SequenceEqual(plan.Exclusions, HistoryPaths.Comparer))
            throw new InvalidOperationException("排除规则已变化，请重新预览恢复范围。");
        var target = ReadCommittedVersion(root, state, preview.VersionId);
        var extractedEntries = DuplicatiEngine.SelectEntriesForSelection(target, plan.SelectedPaths);
        var latest = state.Versions.LastOrDefault() is { } receipt ? ReadCommittedVersion(root, receipt) : null;
        var targetEntries = plan.Target.ToHashSet();
        var replaced = plan.Baseline.Where(entry => !entry.IsDirectory && !targetEntries.Contains(entry)).ToArray();
        var savedEntries = latest?.Entries.ToHashSet() ?? [];
        var savedOriginalPaths = replaced.Where(savedEntries.Contains).Select(entry => entry.Path).ToArray();
        var retainedOriginalPaths = replaced.Where(entry => !savedEntries.Contains(entry)).Select(entry => entry.Path).ToArray();
        await InPlaceRestoreTransaction.EnsurePreviewCurrentAsync(root, plan, token).ConfigureAwait(false);
        var engine = Engine(lease);
        state.ProtectionEnabled = false;
        HistoryStore.Save(root, state);
        ConfigureObservation(root, state, recheck: false);
        var history = HistoryPaths.HistoryDirectory(root);
        var staging = Path.Combine(history, "temp", "in-place-" + Guid.NewGuid().ToString("N"));
        var targetBytes = checked(extractedEntries.Sum(entry => entry.Length) * 2);
        RequireSpace(root, targetBytes, "准备恢复内容与事务暂存");
        string? originalsDirectory = null;
        try
        {
            await RunTaskAsync("恢复到原位置 · " + Path.GetFileName(root), async (progress, cancellation) =>
            {
                SetRuntimeStatus(root, "正在恢复；自动保护已暂停");
                if (savedOriginalPaths.Length > 0)
                {
                    progress.Report(new(null, "正在校验已保存的原件；未保存原件将单独保留"));
                    await VerifyProtectionAsync(root, latest!, engine, cancellation, savedOriginalPaths).ConfigureAwait(false);
                }
                await InPlaceRestoreTransaction.EnsurePreviewCurrentAsync(root, plan, cancellation).ConfigureAwait(false);
                RequireSpace(root, targetBytes, "准备恢复目标与事务暂存");
                progress.Report(new(null, "正在提取并校验恢复内容"));
                await engine.RestoreSelectionAsync(target, history, staging, cancellation, plan.SelectedPaths).ConfigureAwait(false);
                await InPlaceRestoreTransaction.EnsurePreviewCurrentAsync(root, plan, cancellation).ConfigureAwait(false);
                progress.Report(new(null, "正在按已确认的范围恢复工作文件"));
                var result = await InPlaceRestoreTransaction.ApplyAsync(root, plan, staging, retainedOriginalPaths, cancellation).ConfigureAwait(false);
                originalsDirectory = result.OriginalsDirectory;
                if (result.Warning is not null) _context.Log.Write(AppLogLevel.Warning, _context.PluginId, result.Warning);
                return "恢复完成，未创建快照；自动保护已暂停。" +
                    (originalsDirectory is null ? "" : " 未保存原件按原路径保留于：" + originalsDirectory);
            }, token).ConfigureAwait(false);
            _latestRestorePreview.Remove(root);
            _restorePlans.Remove(preview);
            CleanupStaging(staging, extractedEntries);
            SetRuntimeStatus(root, "已恢复，未创建快照；自动保护已暂停");
        }
        catch
        {
            SetRuntimeStatus(root, InPlaceRestoreTransaction.ReadPending(root) is null
                ? "恢复准备未完成，当前工作文件未进入恢复；自动保护已暂停"
                : "恢复未完成，请继续恢复或撤销本次恢复", faulted: true);
            throw;
        }
        NotifyChanged();
        return originalsDirectory;
    });

    public Task<PendingRestoreInfo?> GetPendingRestoreAsync(string root) => ExclusiveAsync(token =>
    {
        _ = Open(root);
        using var ownership = HistoryStore.Lock(root);
        var pending = InPlaceRestoreTransaction.ReadPending(root);
        return Task.FromResult(pending is null ? null : new PendingRestoreInfo(pending.Plan.TargetVersionId, pending.ProtectionVersionId, pending.Status));
    });

    public Task<string?> ResumeRestoreAsync(string root) => FinishRestoreAsync(root, rollback: false);
    public Task AbandonRestoreAsync(string root) => FinishRestoreAsync(root, rollback: true);

    private Task<string?> FinishRestoreAsync(string root, bool rollback) => ExclusiveAsync(async token =>
    {
        _ = Open(root);
        using var ownership = HistoryStore.Lock(root);
        var state = Open(root);
        var pending = InPlaceRestoreTransaction.ReadPending(root) ?? throw new InvalidOperationException("没有未完成恢复。");
        HistoryVersion? protection = null;
        if (pending.ProtectionVersionId.Length != 0)
        {
            var receipt = state.Versions.SingleOrDefault(version => version.Id == pending.ProtectionVersionId);
            if (receipt is not { RestoreProtection: true, Pinned: true })
                throw new InvalidDataException("恢复前保护版本缺失或标记损坏，已停止自动处理，请保留历史库。");
            if (!rollback)
            {
                protection = DuplicatiEngine.ReadVersion(HistoryPaths.HistoryDirectory(root), receipt.Id);
                if (protection is null || !HistoryPaths.Comparer.Equals(protection.SourceRoot, receipt.SourceRoot) ||
                    !protection.Entries.ToHashSet().IsSupersetOf(pending.Plan.Baseline))
                    throw new InvalidDataException("本次恢复前保护版本无法核对，已保留恢复材料。");
            }
        }
        using var lease = protection is null ? null : await RequestEngineAsync(token).ConfigureAwait(false);
        state.ProtectionEnabled = false;
        HistoryStore.Save(root, state);
        ConfigureObservation(root, state, recheck: false);
        string? originalsDirectory = null;
        await RunTaskAsync((rollback ? "撤销未完成恢复 · " : "继续未完成恢复 · ") + Path.GetFileName(root), async (progress, cancellation) =>
        {
            if (protection is not null)
            {
                progress.Report(new(null, "正在重新提取并校验恢复前完整版本"));
                await VerifyProtectionAsync(root, protection!, Engine(lease!), cancellation).ConfigureAwait(false);
            }
            progress.Report(new(null, "正在核对工作文件与恢复记录"));
            string? warning;
            if (rollback) warning = await InPlaceRestoreTransaction.RollbackAsync(root, cancellation).ConfigureAwait(false);
            else
            {
                var result = await InPlaceRestoreTransaction.ResumeAsync(root, cancellation).ConfigureAwait(false);
                warning = result.Warning;
                originalsDirectory = result.OriginalsDirectory;
            }
            if (warning is not null) _context.Log.Write(AppLogLevel.Warning, _context.PluginId, warning);
            return rollback ? pending.Status == "Preparing" ? "已取消尚未执行的恢复，当前工作文件未改动；自动保护仍暂停。"
                : "已撤销本次恢复并校验原状态，自动保护仍暂停。" : "已完成恢复并校验结果，自动保护仍暂停。" +
                    (originalsDirectory is null ? "" : " 未保存原件按原路径保留于：" + originalsDirectory);
        }, token).ConfigureAwait(false);
        ConfigureObservation(root, state, recheck: false);
        SetRuntimeStatus(root, rollback ? "恢复已撤销，自动保护已暂停" : "已恢复，自动保护已暂停");
        NotifyChanged();
        return originalsDirectory;
    });

    private async Task VerifyProtectionAsync(string root, HistoryVersion protection, DuplicatiEngine engine, CancellationToken token,
        IReadOnlyList<string>? selections = null)
    {
        selections ??= [HistorySelection.Root];
        var entries = DuplicatiEngine.SelectEntriesForSelection(protection, selections);
        RequireSpace(root, entries.Sum(entry => entry.Length), "提取并校验已保存的原件");
        var history = HistoryPaths.HistoryDirectory(root);
        var staging = Path.Combine(history, "temp", "verify-protection-" + Guid.NewGuid().ToString("N"));
        await engine.RestoreSelectionAsync(protection, history, staging, token, selections).ConfigureAwait(false);
        CleanupStaging(staging, entries);
    }

    public Task<string> ExportAsync(string root, string versionId, string parentDirectory, string? relativePath = null) =>
        ExportSelectionAsync(root, versionId, parentDirectory, [relativePath ?? HistorySelection.Root]);

    public Task<string> ExportSelectionAsync(string root, string versionId, string parentDirectory, IReadOnlyList<string> relativePaths) =>
        ExportCoreAsync(root, versionId, parentDirectory, relativePaths.ToArray(), saveFile: false);

    public Task<string> ExportFileAsync(string root, string versionId, string destinationFile, string relativePath) =>
        ExportCoreAsync(root, versionId, destinationFile, [HistoryPaths.NormalizeRelative(relativePath)], saveFile: true);

    private Task<string> ExportCoreAsync(string root, string versionId, string destinationPath, string[] paths, bool saveFile) =>
        ExclusiveAsync(async token =>
        {
            _ = Open(root);
            var path = HistoryPaths.Absolute(destinationPath);
            HistoryPaths.RejectHistoryContainer(path);
            HistoryPaths.RequireLocalVolume(path);
            var parent = saveFile ? Path.GetDirectoryName(path) ?? throw new InvalidDataException("请选择另存文件。") : path;
            HistoryPaths.RequireDirectory(parent);
            using var ownership = HistoryStore.Lock(root);
            var version = ReadCommittedVersion(root, Open(root), versionId);
            var selected = DuplicatiEngine.SelectEntriesForSelection(version, paths);
            if (saveFile && !selected.Any(entry => !entry.IsDirectory && HistoryPaths.Comparer.Equals(entry.Path, paths[0])))
                throw new InvalidDataException("请选择单个普通文件另存。");
            var history = HistoryPaths.HistoryDirectory(root);
            var staging = Path.Combine(history, "temp", "restore-" + Guid.NewGuid().ToString("N"));
            var destination = saveFile ? path : Path.Combine(parent, Path.GetFileName(root) + "-" + version.CreatedAt.ToLocalTime().ToString("yyyyMMdd-HHmmss") +
                "-" + Guid.NewGuid().ToString("N")[..8]);
            if (_registry.Folders.Any(folder => HistoryPaths.Overlaps(destination, folder.Root)) ||
                HistoryPaths.Overlaps(destination, _context.Paths.AppRoot) || HistoryPaths.Overlaps(destination, _context.Paths.DataRoot))
                throw new InvalidOperationException("另存位置应与受管理目录、软件目录和应用数据目录互不包含。");
            if (HistoryPaths.Attributes(destination) is not null) throw new IOException("另存位置已经存在，请另选名称。");
            long bytes = selected.Sum(entry => entry.Length);
            RequireSpace(root, bytes, "提取历史内容");
            RequireSpace(parent, bytes, "另存历史内容");
            using var lease = await RequestEngineAsync(token).ConfigureAwait(false);
            await RunTaskAsync("另存历史版本 · " + Path.GetFileName(root), async (progress, cancellation) =>
            {
                progress.Report(new(null, "正在提取并校验历史内容"));
                await Engine(lease).RestoreSelectionAsync(version, history, staging, cancellation, paths).ConfigureAwait(false);
                progress.Report(new(null, "正在另存所选内容"));
                if (saveFile)
                    await RestoreTransaction.SaveFileAsAsync(staging, destination, paths[0], selected, cancellation).ConfigureAwait(false);
                else
                    await RestoreTransaction.SaveAsAsync(staging, destination, null, selected, cancellation).ConfigureAwait(false);
                return destination;
            }, token).ConfigureAwait(false);
            CleanupStaging(staging, selected);
            NotifyChanged();
            return destination;
        });

    public Task<byte[]> ReadFileAsync(string root, string versionId, string relativePath, int maxBytes = TextComparison.MaxBytes,
        CancellationToken cancellationToken = default) => ExclusiveAsync(async token =>
    {
        if (maxBytes is < 1 or > TextComparison.MaxBytes) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        relativePath = HistoryPaths.NormalizeRelative(relativePath);
        _ = Open(root);
        using var ownership = HistoryStore.Lock(root);
        var version = ReadCommittedVersion(root, Open(root), versionId);
        var entry = version.Entries.SingleOrDefault(item => HistoryPaths.Comparer.Equals(item.Path, relativePath))
            ?? throw new InvalidOperationException("这个版本没有所选文件。");
        if (entry.IsDirectory) throw new InvalidOperationException("请选择一个文本文件。");
        if (entry.Length > maxBytes) throw new InvalidOperationException($"文件超过文本预览上限 {maxBytes / 1024} KiB，可另存查看。");
        using var lease = await RequestEngineAsync(token, allowInstall: false).ConfigureAwait(false);
        var staging = Path.Combine(HistoryPaths.HistoryDirectory(root), "temp", "preview-" + Guid.NewGuid().ToString("N"));
        var selected = DuplicatiEngine.SelectEntries(version, relativePath);
        try
        {
            await Engine(lease).RestoreAsync(version, HistoryPaths.HistoryDirectory(root), staging, token, relativePath).ConfigureAwait(false);
            var file = HistoryPaths.ResolveWorkPath(staging, relativePath);
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
            if (stream.Length != entry.Length || stream.Length > maxBytes) throw new IOException("提取文件大小变化，无法预览。");
            var result = new byte[(int)stream.Length];
            await stream.ReadExactlyAsync(result, token).ConfigureAwait(false);
            if (Convert.ToHexString(SHA256.HashData(result)) != entry.Hash) throw new IOException("提取文件内容变化，无法预览。");
            return result;
        }
        finally { CleanupStaging(staging, selected); }
    }, cancellationToken);
}
