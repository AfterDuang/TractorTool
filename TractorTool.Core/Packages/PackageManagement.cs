using System.Text.Json;
using TractorTool.Core.Storage;
using TractorTool.Contracts.Packages;
using TractorTool.Contracts;
using TractorTool.Core.Packages.Python;

namespace TractorTool.Core.Packages;

public sealed record PackageDependency(PackageReference Package, string PluginId, string PluginName, IReadOnlyList<GitHubPackage>? Downloads = null);
public sealed record PackageReferenceSnapshot(IReadOnlyList<PackageDependency> Dependencies, bool Complete, string Error = "")
{
    public IReadOnlyList<PythonEnvironmentDefinition> PythonEnvironments { get; init; } = [];
}
public sealed record PackageListItem(PackageReference Package, PackageDefinition? Definition, PackageStatus Status,
    IReadOnlyList<string> RequiredBy, bool IsInstalled, bool CanInstall, bool CanRemove);
public sealed record PackageInstallPrompt(PackageDefinition Definition, string RequestedBy, bool IsRepair);

public sealed partial class ExternalPackageService
{
    private readonly string _rid;
    private readonly Dictionary<PackageReference, Receipt?> _pendingReceipts = [];
    private PackageReferenceSnapshot _references = new([], false, "正在读取插件依赖。");
    public string SourceError
    {
        get { lock (_gate) return string.Join("\n", new[] { _sourceError, _settingsError, _pendingReceipts.Count == 0 ? "" : "部分安装记录缺少有效资料，需对应插件提供一致的下载声明；文件已保留。" }.Where(s => s.Length != 0)); }
        private set { _sourceError = value; }
    }
    public Func<PackageInstallPrompt, CancellationToken, Task<bool>>? ConfirmInstallAsync { get; set; }

    public void UpdateReferences(PackageReferenceSnapshot snapshot)
    {
        lock (_gate)
        {
            _references = snapshot with { Dependencies = snapshot.Dependencies.Select(d => d with
                { Downloads = d.Downloads?.Select(p => p.Snapshot(d.Package)).ToArray() }).ToArray() };
            if (_references.Dependencies.Select(d => d.Package).Any(HasConflictingIdentity))
                _references = _references with { Complete = false, Error = "依赖标识或版本的大小写与已有记录冲突，请插件作者统一拼写；暂不可清理。" };
            RestoreDeclaredInstallations();
        }
        Python.UpdateReferences(snapshot.PythonEnvironments, snapshot.Complete, snapshot.Error);
        Notify();
    }
    internal void InvalidateReferences()
    {
        lock (_gate) _references = _references with { Complete = false, Error = "插件依赖正在变更，暂不可清理。" };
        Python.InvalidateReferences();
        Notify();
    }
    public string ReferenceError { get { lock (_gate) return _references.Complete ? "" : _references.Error; } }

    private bool HasConflictingIdentity(PackageReference package) => OperatingSystem.IsWindows() &&
        _entries.Keys.Concat(_resolutions.Keys).Concat(_pendingReceipts.Keys).Concat(_references.Dependencies.Select(d => d.Package))
            .Any(other => other != package && string.Equals(other.Id, package.Id, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(other.Version, package.Version, StringComparison.OrdinalIgnoreCase));
    public IReadOnlyList<PackageListItem> GetPackages()
    {
        lock (_gate)
        {
            var keys = _references.Dependencies.Select(d => d.Package).Concat(_entries.Where(p =>
                p.Value.HasFiles || p.Value.Requested || p.Value.Operation is not null).Select(p => p.Key)).Distinct();
            return keys.OrderBy(p => p.Id).ThenBy(p => p.Version).Select(key =>
            {
                _entries.TryGetValue(key, out var e);
                var required = _references.Dependencies.Where(d => d.Package == key)
                    .Select(d => $"{d.PluginName} ({d.PluginId})").Distinct().Order().ToArray();
                return new PackageListItem(key, e?.Definition, Status(key), required, e?.HasFiles == true,
                    e is { Operation: null, Preparation: null } && (e.SourceAvailable || HasDownload(key)) && e.Users.Count == 0 &&
                        CanManageFiles(e) && !NativePackageRetention.IsRetained(e.Directory) && !_ambiguousInstallations.Contains(key) && e.Phase != PackagePhase.Ready,
                    e is not null && CanRemove(e));
            }).ToArray();
        }
    }
    private PackageStatus Status(PackageReference key)
    {
        if (!_entries.TryGetValue(key, out var e))
            return new(key, _rid, PackagePhase.Missing, 0, null, "", [], false, "", null);
        return new(key, e.Definition.Rid, e.Phase, e.Downloaded, e.Total, e.Error,
            Array.AsReadOnly(e.Users.Keys.Order().ToArray()), e.InstallCancellation is not null, e.Directory, e.InstalledBytes)
            { Kind = e.Definition.Kind, RetainedBy = NativePackageRetention.GetOwners(e.Directory) };
    }
    private bool CanRemove(Entry e) => _shutdown is null && _references.Complete && e.HasFiles &&
        !_references.Dependencies.Any(d => d.Package == e.Definition.Package) &&
        e.Operation is null && e.Users.Count == 0 && e.Requests == 0 && e.Preparation is null &&
        CanManageFiles(e) && !NativePackageRetention.IsRetained(e.Directory) && !_ambiguousInstallations.Contains(e.Definition.Package);

    // Preserve the API 1.3 contract for already-built plugins.
    public async Task<IPackageLease> RequestAsync(PackageReference package, string owner, CancellationToken token = default) =>
        await TryRequestAsync(package, owner, token).ConfigureAwait(false) ?? throw new OperationCanceledException("已取消依赖安装。");

    // Reservation spans consent, shared installation and lease acquisition. One caller cancelling
    // its wait must not stop another plugin's download or expose the ready package to cleanup.
    public async Task<IPackageLease?> TryRequestAsync(PackageReference package, string owner, CancellationToken token = default,
        bool supportsNativeLibraries = true)
    {
        token.ThrowIfCancellationRequested();
        await EnsureSourceAsync(package, token, supportsNativeLibraries).ConfigureAwait(false);
        Entry e; Task<bool> prepare;
        lock (_gate)
        {
            EnsureRunning(); e = Find(package);
            RequireSupportedKind(e.Definition.Kind, supportsNativeLibraries);
            RequireOwnedFiles(e);
            _ = DeclaredDownload(package, e.Definition);
            if (e.Operation is not null && e.InstallCancellation is null)
                throw new InvalidOperationException("此依赖正在清理，请完成后再申请。");
            if (e.Phase != PackagePhase.Ready) RequireNotRetained(e);
            e.Requests++; e.Requested = true;
            if (e.Phase == PackagePhase.Ready && e.Operation is null) prepare = Task.FromResult(true);
            else prepare = e.Preparation ??= StartPreparation(e, owner);
        }
        Notify();
        try
        {
            var accepted = await prepare.WaitAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (!accepted) return null;
            return await AcquireAsync(package, owner, token, supportsNativeLibraries).ConfigureAwait(false);
        }
        finally { lock (_gate) e.Requests--; Notify(); }
    }
    private Task<bool> StartPreparation(Entry e, string owner)
    {
        var definition = e.SourceDefinition;
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _operations.Add(completion.Task);
        _ = Task.Run(async () =>
        {
            Exception? failure = null;
            var accepted = false;
            try
            {
                if (definition is null) throw new NotSupportedException("可信安装源暂未提供此包版本。");
                var confirm = ConfirmInstallAsync ?? throw new InvalidOperationException("宿主暂时无法确认依赖安装。");
                if (!await confirm(new(definition, owner, e.HasFiles), _stopping.Token).ConfigureAwait(false))
                    return;
                _stopping.Token.ThrowIfCancellationRequested();
                // Every requester may have left while the confirmation dialog was visible.
                lock (_gate) if (e.Requests == 0) throw new OperationCanceledException("依赖申请已取消。");
                await InstallAsync(e.Definition.Package, _stopping.Token, repair: true, definition).ConfigureAwait(false);
                accepted = true;
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                lock (_gate)
                {
                    e.Preparation = null; _operations.Remove(completion.Task);
                    if (failure is OperationCanceledException canceled) completion.TrySetCanceled(canceled.CancellationToken);
                    else if (failure is not null) completion.TrySetException(failure);
                    else completion.TrySetResult(accepted);
                }
                Notify();
            }
        });
        _ = completion.Task.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
        return completion.Task;
    }

    private void RestoreInstalledDefinitions()
    {
        // The marker adds exactly one owned level for native packages. Legacy DLL/<version>
        // remains a tool package; unknown files are never recursively adopted or deleted.
        try
        {
            if (!Directory.Exists(_root)) return;
            foreach (var id in ReadDirectories(_root).Where(p => !Path.GetFileName(p).StartsWith('.')))
            {
                try
                {
                    if (string.Equals(Path.GetFileName(id), "uv", StringComparison.OrdinalIgnoreCase) &&
                        PythonEnvironmentService.IsOwnedRoot(id)) continue;
                    if (string.Equals(Path.GetFileName(id), "DLL", StringComparison.OrdinalIgnoreCase) && IsNativeRoot(id))
                    {
                        foreach (var nativeId in ReadDirectories(id).Where(p => !Path.GetFileName(p).StartsWith('.')))
                            RestorePackageVersions(nativeId, PackageKind.NativeLibrary);
                    }
                    else RestorePackageVersions(id, PackageKind.Tool);
                }
                catch (Exception ex)
                {
                    SourceError = "部分依赖目录不可读取，已保留文件：" + ex.Message;
                    _log.Write(AppLogLevel.Warning, "Packages", SourceError, ex);
                }
            }
        }
        catch (Exception ex) { SourceError = "读取依赖安装目录失败：" + ex.Message; _log.Write("Packages", SourceError, ex); }
    }

    private void RestorePackageVersions(string id, PackageKind kind)
    {
        foreach (var version in ReadDirectories(id).Where(p => !Path.GetFileName(p).StartsWith('.')))
        {
            var directory = Path.Combine(version, _rid);
            var key = new PackageReference(Path.GetFileName(id), Path.GetFileName(version));
            try { RecoverReplacement(directory); }
            catch (Exception ex) { InvalidInstallation(key, ex); continue; }
            ReadInstalledDefinition(key, directory, kind);
        }
    }

    // Called under _gate, before selecting an install prompt or handing out a lease.
    private void RecoverBeforeRequest(PackageReference key)
    {
        if (_ambiguousInstallations.Contains(key)) throw new InvalidDataException("同一依赖存在多种安装目录，已保留文件，请先解决标识冲突。");
        if (HasConflictingIdentity(key))
            throw new InvalidDataException("依赖标识或版本的大小写与已有记录冲突，请插件作者统一拼写。");
        _entries.TryGetValue(key, out var known);
        if (known is not null && (known.Phase == PackagePhase.Ready || known.Operation is not null ||
            known.Preparation is not null || known.Requests != 0 || known.Users.Count != 0)) return;
        AppPaths.ValidateId(key.Id); AppPaths.ValidateId(key.Version);
        var definition = known?.Definition ?? DeclaredDownload(key)?.Definition(key);
        var directory = definition is null ? PackageFiles.SafePath(_root, $"{key.Id}/{key.Version}/{_rid}") : PackageDirectory(definition);
        if (!RecoverReplacement(directory)) return;
        ReadInstalledDefinition(key, directory, definition?.Kind ?? PackageKind.Tool);
        if (!_entries.ContainsKey(key)) RestoreDeclaredInstallations();
    }

    private void ReadInstalledDefinition(PackageReference key, string directory, PackageKind kind)
    {
        _entries.TryGetValue(key, out var known);
        try
        {
            if (!HasDirectory(directory)) return;
            var previousDirectory = _installedLocations.GetValueOrDefault(key) ?? known?.Directory;
            if (previousDirectory is not null && !string.Equals(previousDirectory, directory,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            {
                _ambiguousInstallations.Add(key);
                throw new InvalidDataException("同一依赖存在多种安装目录，已保留文件，不能复用或清理。");
            }
            _installedLocations[key] = directory;
            if (known is not null) known.HasFiles = true;
            _pendingReceipts[key] = null;
            var path = PackageFiles.SafePath(directory, "receipt.json");
            if (new FileInfo(path).Length > 65536) throw new InvalidDataException("安装记录过大。");
            var receipt = JsonSerializer.Deserialize<Receipt>(File.ReadAllText(path)) ?? throw new InvalidDataException("安装记录为空。");
            _pendingReceipts[key] = receipt;
            if (receipt.Schema == 1 && receipt.Definition is null && known is null)
                return;
            var definition = receipt.Definition ?? known?.Definition ?? throw new InvalidDataException("旧安装缺少源元数据，已保留文件。");
            PackageDefinitions.Validate(definition);
            if (definition.Package != key || definition.Rid != _rid || definition.Kind != kind ||
                !string.Equals(definition.Sha256, receipt.ArchiveHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("安装记录与目录身份不符。");
            var entry = new Entry(definition, directory) { HasFiles = true,
                SourceDefinition = string.Equals(known?.SourceDefinition?.Sha256, definition.Sha256, StringComparison.OrdinalIgnoreCase) ? known?.SourceDefinition : null };
            _entries[key] = entry; _pendingReceipts.Remove(key);
            try
            {
                var verified = ReadReceipt(entry)!;
                entry.Phase = PackagePhase.Ready; entry.InstalledBytes = verified.InstalledBytes;
                if (receipt.Schema == 1)
                    UpgradeReceipt(directory, path, receipt with { Schema = 2, Definition = definition });
            }
            catch (Exception ex) { entry.Phase = PackagePhase.Failed; entry.Error = ex.Message; }
        }
        catch (Exception ex) { InvalidInstallation(key, ex); }
    }

    private void InvalidInstallation(PackageReference key, Exception ex)
    {
        if (_entries.TryGetValue(key, out var entry)) { entry.Phase = PackagePhase.Failed; entry.Error = ex.Message; }
        _log.Write(AppLogLevel.Warning, "Packages", "安装记录无法读取，已保留文件：" + key.Id, ex);
    }

    private void RestoreDeclaredInstallations()
    {
        foreach (var key in _references.Dependencies.Select(d => d.Package).Distinct())
        {
            try
            {
                if (_entries.ContainsKey(key) || HasConflictingIdentity(key)) continue;
                if (DeclaredDownload(key) is not { } recipe) continue;
                var definition = recipe.Definition(key);
                var directory = PackageDirectory(definition);
                PackageFiles.CheckPath(directory);
                if (!Directory.Exists(directory)) continue;
                // A damaged/missing receipt must not hide existing files or bypass repair consent.
                var entry = new Entry(definition, directory) { HasFiles = true };
                _entries[key] = entry; _pendingReceipts.Remove(key);
                try
                {
                    RecoverReplacement(directory);
                    var receipt = ReadReceipt(entry) ?? throw new InvalidDataException("旧安装目录已不存在。");
                    entry.Phase = PackagePhase.Ready; entry.InstalledBytes = receipt.InstalledBytes;
                    if (receipt.Schema == 1)
                        UpgradeReceipt(directory, PackageFiles.SafePath(directory, "receipt.json"), receipt with { Schema = 2, Definition = definition });
                }
                catch (Exception ex) { entry.Phase = PackagePhase.Failed; entry.Error = ex.Message; }
            }
            catch (Exception ex)
            { _log.Write(AppLogLevel.Warning, "Packages", "旧依赖安装资料未恢复，文件已保留：" + key.Id, ex); }
        }
    }

    private void UpgradeReceipt(string directory, string path, Receipt receipt)
    {
        try
        {
            var temporary = PackageFiles.SafePath(directory, "receipt-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(receipt));
                File.Move(temporary, path, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        catch (Exception ex)
        {
            // v1 is still readable. An optional metadata upgrade must not disable a valid tool.
            SourceError = "旧安装记录暂未升级，工具仍可使用：" + ex.Message;
            _log.Write("Packages", SourceError, ex);
        }
    }

    private string[] ReadDirectories(string path)
    {
        try { PackageFiles.CheckPath(path); return Directory.GetDirectories(path); }
        catch (Exception ex)
        {
            SourceError = "部分依赖目录不可读取，已保留文件：" + ex.Message;
            _log.Write("Packages", SourceError, ex); return [];
        }
    }
}
