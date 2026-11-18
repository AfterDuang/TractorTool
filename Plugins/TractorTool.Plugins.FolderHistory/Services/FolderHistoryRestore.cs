using System.Security.Cryptography;
using TractorTool.Contracts;
using TractorTool.Plugins.FolderHistory.Models;
using TractorTool.Plugins.FolderHistory.Processing;
using TractorTool.Plugins.FolderHistory.Storage;

namespace TractorTool.Plugins.FolderHistory.Services;

public sealed partial class FolderHistoryService
{
    private readonly Dictionary<Guid, (RestorePreview Preview, InPlaceRestorePlan Plan)> _restorePreviews = [];

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
            var preview = new RestorePreview(Guid.NewGuid(), root, versionId, plan.Selection, plan.Changes)
                { RelativePaths = Array.AsReadOnly(plan.SelectedPaths.ToArray()) };
            foreach (var id in _restorePreviews.Where(pair => HistoryPaths.Comparer.Equals(pair.Value.Preview.Root, root)).Select(pair => pair.Key).ToArray())
                _restorePreviews.Remove(id);
            _restorePreviews[preview.Id] = (preview, plan);
            return preview;
        });
    }

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
        var target = ReadCommittedVersion(root, state, prepared.Preview.VersionId);
        var extractedEntries = DuplicatiEngine.SelectEntriesForSelection(target, plan.SelectedPaths);
        await InPlaceRestoreTransaction.EnsurePreviewCurrentAsync(root, plan, token).ConfigureAwait(false);
        var engine = Engine(lease);
        state.ProtectionEnabled = false;
        HistoryStore.Save(root, state);
        ConfigureObservation(root, state, recheck: false);
        var protection = await CaptureAsync(root, state, engine, "恢复前完整保护", true, token).ConfigureAwait(false);
        if (!protection.Version.Entries.ToHashSet().IsSupersetOf(plan.Baseline))
            throw new IOException("恢复前版本未包含预览中的当前内容，未覆盖工作文件。请重新预览。");
        await InPlaceRestoreTransaction.EnsurePreviewCurrentAsync(root, plan, token).ConfigureAwait(false);
        var history = HistoryPaths.HistoryDirectory(root);
        var staging = Path.Combine(history, "temp", "in-place-" + Guid.NewGuid().ToString("N"));
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
                await engine.RestoreSelectionAsync(target, history, staging, cancellation, plan.SelectedPaths).ConfigureAwait(false);
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
                !protection.Entries.ToHashSet().IsSupersetOf(pending.Plan.Baseline))
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

    public Task<byte[]> ReadFileAsync(string root, string versionId, string relativePath, int maxBytes = 256 * 1024,
        CancellationToken cancellationToken = default) => ExclusiveAsync(async token =>
    {
        if (maxBytes is < 1 or > 256 * 1024) throw new ArgumentOutOfRangeException(nameof(maxBytes));
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
