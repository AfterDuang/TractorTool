using System.Text.Json;
using TractorTool.Contracts.Packages;

namespace TractorTool.Core.Packages;

public sealed record PackageDependency(PackageReference Package, string PluginId, string PluginName);
public sealed record PackageReferenceSnapshot(IReadOnlyList<PackageDependency> Dependencies, bool Complete, string Error = "");
public sealed record PackageListItem(PackageReference Package, PackageDefinition? Definition, PackageStatus Status,
    IReadOnlyList<string> RequiredBy, bool IsInstalled, bool CanInstall, bool CanRemove);
public sealed record PackageInstallPrompt(PackageDefinition Definition, string RequestedBy, bool IsRepair);

public sealed partial class ExternalPackageService
{
    private readonly string _rid;
    private PackageReferenceSnapshot _references = new([], false, "正在读取插件依赖。");
    public string SourceError { get; private set; } = "";
    public Func<PackageInstallPrompt, CancellationToken, Task<bool>>? ConfirmInstallAsync { get; set; }

    public void UpdateReferences(PackageReferenceSnapshot snapshot)
    {
        lock (_gate) _references = snapshot with { Dependencies = snapshot.Dependencies.ToArray() };
        Notify();
    }
    internal void InvalidateReferences()
    {
        lock (_gate) _references = _references with { Complete = false, Error = "插件依赖正在变更，暂不可清理。" };
        Notify();
    }
    public string ReferenceError { get { lock (_gate) return _references.Complete ? "" : _references.Error; } }
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
                    e is { SourceAvailable: true, Operation: null, Preparation: null } && e.Users.Count == 0 && e.Phase != PackagePhase.Ready,
                    e is not null && CanRemove(e));
            }).ToArray();
        }
    }
    private PackageStatus Status(PackageReference key)
    {
        if (!_entries.TryGetValue(key, out var e))
            return new(key, _rid, PackagePhase.Missing, 0, null, "可信安装源暂未提供此版本或平台。", [], false, "", null);
        return new(key, e.Definition.Rid, e.Phase, e.Downloaded, e.Total, e.Error,
            Array.AsReadOnly(e.Users.Keys.Order().ToArray()), e.InstallCancellation is not null, e.Directory, e.InstalledBytes);
    }
    private bool CanRemove(Entry e) => _shutdown is null && _references.Complete && e.HasFiles &&
        !_references.Dependencies.Any(d => d.Package == e.Definition.Package) &&
        e.Operation is null && e.Users.Count == 0 && e.Requests == 0 && e.Preparation is null;

    // Preserve the API 1.3 contract for already-built plugins.
    public async Task<IPackageLease> RequestAsync(PackageReference package, string owner, CancellationToken token = default) =>
        await TryRequestAsync(package, owner, token).ConfigureAwait(false) ?? throw new OperationCanceledException("已取消依赖安装。");

    // Reservation spans consent, shared installation and lease acquisition. One caller cancelling
    // its wait must not stop another plugin's download or expose the ready package to cleanup.
    public async Task<IPackageLease?> TryRequestAsync(PackageReference package, string owner, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        Entry e; Task<bool> prepare;
        lock (_gate)
        {
            EnsureRunning(); e = Find(package);
            if (e.Operation is not null && e.InstallCancellation is null)
                throw new InvalidOperationException("此依赖正在清理，请完成后再申请。");
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
            return await AcquireAsync(package, owner, token).ConfigureAwait(false);
        }
        finally { lock (_gate) e.Requests--; Notify(); }
    }
    private Task<bool> StartPreparation(Entry e, string owner)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _operations.Add(completion.Task);
        _ = Task.Run(async () =>
        {
            Exception? failure = null;
            var accepted = false;
            try
            {
                if (!e.SourceAvailable) throw new NotSupportedException("可信安装源暂未提供此包版本。");
                var confirm = ConfirmInstallAsync ?? throw new InvalidOperationException("宿主暂时无法确认依赖安装。");
                if (!await confirm(new(e.SourceDefinition!, owner, e.HasFiles), _stopping.Token).ConfigureAwait(false))
                    return;
                _stopping.Token.ThrowIfCancellationRequested();
                // Every requester may have left while the confirmation dialog was visible.
                lock (_gate) if (e.Requests == 0) throw new OperationCanceledException("依赖申请已取消。");
                await InstallAsync(e.Definition.Package, _stopping.Token, repair: true).ConfigureAwait(false);
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

    private IReadOnlyList<PackageDefinition> LoadSource()
    {
        try { return PackageCatalog.Load(Path.Combine(AppContext.BaseDirectory, "PackageSources", "default.json")); }
        catch (Exception ex) { SourceError = "依赖源不可用：" + ex.Message; _log.Write("Packages", SourceError, ex); return []; }
    }

    private void RestoreInstalledDefinitions()
    {
        // Enumerate only the three owned directory levels. Never recurse through links or delete
        // unknown files; damaged/legacy records without usable metadata remain on disk.
        try
        {
            if (!Directory.Exists(_root)) return;
            foreach (var id in ReadDirectories(_root).Where(p => !Path.GetFileName(p).StartsWith('.')))
            {
                foreach (var version in ReadDirectories(id))
                {
                    var directory = Path.Combine(version, _rid);
                    var key = new PackageReference(Path.GetFileName(id), Path.GetFileName(version));
                    _entries.TryGetValue(key, out var known);
                    try
                    {
                        PackageFiles.CheckPath(version);
                        if (!Directory.Exists(directory)) continue;
                        var path = PackageFiles.SafePath(directory, "receipt.json");
                        if (new FileInfo(path).Length > 65536) throw new InvalidDataException("安装记录过大。");
                        var receipt = JsonSerializer.Deserialize<Receipt>(File.ReadAllText(path)) ?? throw new InvalidDataException("安装记录为空。");
                        var definition = receipt.Definition ?? known?.Definition ?? throw new InvalidDataException("旧安装缺少源元数据，已保留文件。");
                        PackageCatalog.Validate(definition);
                        if (definition.Package != key || definition.Rid != _rid || !string.Equals(definition.Sha256, receipt.ArchiveHash, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("安装记录与目录身份不符。");
                        var entry = new Entry(definition, directory) { HasFiles = true,
                            SourceDefinition = string.Equals(known?.SourceDefinition?.Sha256, definition.Sha256, StringComparison.OrdinalIgnoreCase) ? known?.SourceDefinition : null };
                        _entries[key] = entry;
                        try
                        {
                            var verified = ReadReceipt(entry)!;
                            entry.Phase = PackagePhase.Ready; entry.InstalledBytes = verified.InstalledBytes;
                            if (receipt.Schema == 1)
                                UpgradeReceipt(directory, path, receipt with { Schema = 2, Definition = definition });
                        }
                        catch (Exception ex) { entry.Phase = PackagePhase.Failed; entry.Error = ex.Message; }
                    }
                    catch (Exception ex)
                    {
                        if (known is not null) { known.Phase = PackagePhase.Failed; known.Error = ex.Message; }
                        SourceError = "部分安装记录无法读取，已保留文件：" + ex.Message; _log.Write("Packages", SourceError, ex);
                    }
                }
            }
        }
        catch (Exception ex) { SourceError = "读取依赖安装目录失败：" + ex.Message; _log.Write("Packages", SourceError, ex); }
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
