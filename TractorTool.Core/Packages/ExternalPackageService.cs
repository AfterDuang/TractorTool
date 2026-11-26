using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text.Json;
using TractorTool.Contracts;
using TractorTool.Contracts.Packages;
using TractorTool.Contracts.Processes;
using TractorTool.Core.Storage;
using TractorTool.Core.Packages.Python;
using TractorTool.Core.Packages.Node;

namespace TractorTool.Core.Packages;

/// <summary>One host-owned service per data root. The host holds its data-root instance lock.</summary>
public sealed partial class ExternalPackageService : IAsyncDisposable
{
    private readonly string _root;
    private readonly HttpClient _http;
    private readonly IProcessRunner _processes;
    private readonly IAppLog _log;
    private readonly object _gate = new();
    private readonly Dictionary<PackageReference, Entry> _entries;
    private readonly CancellationTokenSource _stopping = new();
    private readonly HashSet<Task> _operations = [];
    private Task? _shutdown;
    private readonly Task _recovery;
    private const string WorkMarker = "TractorTool.PackageWork.v1";
    private const string RepairMarker = "TractorTool.PackageRepair.v1";
    public event Action? Changed;
    public PythonEnvironmentService Python { get; }
    public NodeEnvironmentService Node { get; }

    public ExternalPackageService(string root, string rid, HttpClient http, IProcessRunner processes, IAppLog log,
        IEnumerable<PackageDefinition>? definitions = null, string? sourcesFile = null)
    {
        _root = AppPaths.Absolute(root); PackageFiles.CheckPath(_root);
        _http = http; _processes = processes; _log = log;
        _rid = rid;
        _sourcesFile = AppPaths.Absolute(sourcesFile ?? Path.Combine(_root, ".sources.json"));
        LoadSources();
        var supplied = (definitions ?? []).Select(PackageDefinitions.Snapshot).Where(d => d.Rid == rid).ToArray();
        _entries = [];
        foreach (var d in supplied)
        {
            var entry = new Entry(d, PackageDirectory(d));
            if (!_entries.TryAdd(d.Package, entry)) throw new InvalidDataException("外部工具目录重复。");
            entry.SourceDefinition = d;
            entry.HasFiles = Directory.Exists(entry.Directory);
            try
            {
                foreach (var tool in d.Tools.Values) _ = PackageFiles.SafePath(entry.Directory, tool.Path);
                var receipt = ReadReceipt(entry);
                entry.InstalledBytes = receipt?.InstalledBytes;
                entry.Phase = receipt is null ? PackagePhase.Missing : PackagePhase.Ready;
            }
            catch (Exception ex) { entry.Phase = PackagePhase.Failed; entry.Error = ex.Message; }
        }
        RestoreInstalledDefinitions();
        Python = new(_root, rid, http, processes, log, IsPythonSourceAllowed);
        Python.Changed += Notify;
        Node = new(_root, rid, http, processes, log, IsNodeSourceAllowed);
        Node.Changed += Notify;
        _recovery = Task.Run(RecoverWork);
        _operations.Add(_recovery);
    }

    public PackageStatus GetStatus(PackageReference package)
    {
        lock (_gate)
        {
            return Status(package);
        }
    }

    // Caller cancellation only cancels waiting. Explicit CancelInstall cancels the shared transfer for all waiters.
    public async Task InstallAsync(PackageReference package, CancellationToken cancellationToken = default)
    {
        await EnsureSourceAsync(package, cancellationToken).ConfigureAwait(false);
        await InstallAsync(package, cancellationToken, repair: false).ConfigureAwait(false);
    }

    private Task InstallAsync(PackageReference package, CancellationToken cancellationToken, bool repair,
        PackageDefinition? approvedDefinition = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task task;
        lock (_gate)
        {
            EnsureRunning(); var e = Find(package);
            if (e.Phase == PackagePhase.Ready && e.Operation is null) return Task.CompletedTask;
            var definition = approvedDefinition ?? e.SourceDefinition ?? throw new NotSupportedException("插件尚未提供此包版本的安装资料。");
            _ = DeclaredDownload(package, definition);
            if (e.Operation is null)
            {
                RequireOwnedFiles(e);
                RequireNotRetained(e);
                if (e.Users.Count != 0) throw new InvalidOperationException("依赖仍被使用，不能重新安装。");
                e.InstallCancellation = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
                e.Phase = PackagePhase.Downloading; e.Error = ""; e.Downloaded = 0; e.Total = null;
                e.Operation = StartOperation(async () =>
                {
                    _log.Write("Packages", $"{package.Id} {package.Version}：开始下载并安装依赖。");
                    await InstallCoreAsync(e, definition, repair && e.HasFiles, e.InstallCancellation.Token).ConfigureAwait(false);
                }, e);
            }
            else if (e.InstallCancellation is null) throw new InvalidOperationException("依赖正在清理。");
            task = e.Operation;
        }
        Notify();
        return task.WaitAsync(cancellationToken);
    }

    public void CancelInstall(PackageReference package)
    {
        lock (_gate) Find(package).InstallCancellation?.Cancel();
    }

    public Task RemoveAsync(PackageReference package)
    {
        Task task;
        lock (_gate)
        {
            EnsureRunning(); var e = Find(package);
            if (!CanRemove(e)) throw new InvalidOperationException("依赖仍被插件引用、正在使用或引用状态不可确认，不能清理。");
            e.Phase = PackagePhase.Installing; e.Error = "";
            task = e.Operation = StartOperation(() =>
            {
                RemoveFiles(e);
                SetState(e, PackagePhase.Missing);
                lock (_gate) { e.HasFiles = false; e.Requested = false; }
                _log.Write("Packages", $"{package.Id} {package.Version}：未引用依赖已清理。");
                return Task.CompletedTask;
            }, e);
        }
        Notify(); return task;
    }

    private void RemoveFiles(Entry e)
    {
        _ = PackageDirectory(e.Definition);
        RequireNotRetained(e);
        RecoverReplacement(e.Directory);
        PackageFiles.CheckPath(e.Directory);
        if (Directory.Exists(e.Directory))
        {
            var trash = NewWorkDirectory();
            try
            {
                Directory.Move(e.Directory, Path.Combine(trash, "removed"));
                lock (_gate) e.HasFiles = false;
                PackageFiles.DeleteOwnedTree(_root, trash, "owner.txt");
            }
            catch { Cleanup(trash); throw; }
        }
        lock (_gate) e.HasFiles = false;
    }

    internal Task<IPackageLease> AcquireAsync(PackageReference package, string owner, CancellationToken token, bool supportsNativeLibraries = true)
    {
        token.ThrowIfCancellationRequested();
        lock (_gate)
        {
            EnsureRunning();
            RecoverBeforeRequest(package);
            var e = Find(package);
            RequireSupportedKind(e.Definition.Kind, supportsNativeLibraries);
            _ = DeclaredDownload(package, e.Definition);
            if (e.Phase != PackagePhase.Ready || e.Operation is not null) throw new InvalidOperationException("请先在设置中安装所需的外部工具。");
            e.Users[owner] = e.Users.GetValueOrDefault(owner) + 1;
            var completion = new TaskCompletionSource<IPackageLease>(TaskCreationOptions.RunContinuationsAsynchronously);
            _operations.Add(completion.Task);
            _ = Task.Run(async () =>
            {
                Notify();
                try
                {
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _stopping.Token);
                    var receipt = ReadReceipt(e) ?? throw new InvalidDataException("工具安装记录缺失。");
                    foreach (var (name, tool) in e.Definition.Tools)
                    {
                        linked.Token.ThrowIfCancellationRequested();
                        var path = PackageFiles.SafePath(e.Directory, tool.Path);
                        if (!string.Equals(await HashAsync(path, linked.Token), receipt.Tools[name], StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("工具文件校验失败，请在外部依赖设置中重新安装。");
                    }
                    if (e.Definition.Kind == PackageKind.NativeLibrary)
                        await NativePackageIntegrity.ValidateAsync(e.Directory, e.Definition, receipt.ManifestHash!, linked.Token).ConfigureAwait(false);
                    linked.Token.ThrowIfCancellationRequested();
                    completion.TrySetResult(new Lease(e.Directory, new ReadOnlyDictionary<string, string>(e.Definition.Tools.ToDictionary(
                        p => p.Key, p => PackageFiles.SafePath(e.Directory, p.Value.Path))),
                        new ReadOnlyDictionary<string, string>(e.Definition.Libraries.ToDictionary(
                            p => p.Key, p => PackageFiles.SafePath(e.Directory, p.Value))),
                        () => Release(e, owner), () => Retain(e, owner), Notify));
                }
                catch (OperationCanceledException ex) { Release(e, owner); completion.TrySetCanceled(ex.CancellationToken); }
                catch (Exception ex)
                {
                    // Publish failure and release the reservation together; no old failure may overwrite a new install.
                    lock (_gate)
                    {
                        e.Phase = PackagePhase.Failed; e.Error = ex.Message;
                        if (--e.Users[owner] == 0) e.Users.Remove(owner);
                    }
                    Notify(); completion.TrySetException(ex);
                }
                finally { lock (_gate) _operations.Remove(completion.Task); }
            });
            return completion.Task;
        }
    }

    public PackageScope CreateScope(string owner, IReadOnlyList<PackageReference> allowed, CancellationToken stoppingToken) =>
        new(this, owner, allowed, stoppingToken);
    public PackageScope CreateScope(string owner, IReadOnlyList<PackageReference> allowed, CancellationToken stoppingToken, bool supportsNativeLibraries) =>
        new(this, owner, allowed, stoppingToken, supportsNativeLibraries);
    public PackageScope CreateScope(string owner, IReadOnlyList<PackageReference> allowed, CancellationToken stoppingToken,
        bool supportsNativeLibraries, IReadOnlyList<PythonEnvironmentDefinition> pythonEnvironments, IProcessRunner pythonProcesses) =>
        new(this, owner, allowed, stoppingToken, supportsNativeLibraries, pythonEnvironments, pythonProcesses);
    public PackageScope CreateScope(string owner, IReadOnlyList<PackageReference> allowed, CancellationToken stoppingToken,
        bool supportsNativeLibraries, IReadOnlyList<PythonEnvironmentDefinition> pythonEnvironments, IProcessRunner pythonProcesses,
        IReadOnlyList<NodeEnvironmentDefinition> nodeEnvironments, IProcessRunner nodeProcesses) =>
        new(this, owner, allowed, stoppingToken, supportsNativeLibraries, pythonEnvironments, pythonProcesses, nodeEnvironments, nodeProcesses);

    internal bool IsPythonSourceAllowed(string source)
    {
        lock (_gate) return _settingsError.Length == 0 && _sources.Any(s => s.Kind == DependencySourceKind.PythonIndex &&
            DependencySource.IndexUri(s.Url).AbsoluteUri == source);
    }

    internal bool IsNodeSourceAllowed(string source)
    {
        lock (_gate) return _settingsError.Length == 0 && _sources.Any(s => s.Kind == DependencySourceKind.NpmRegistry &&
            NodeEnvironmentDefinitions.RegistryUri(s.Url).AbsoluteUri == source);
    }

    private Task StartOperation(Func<Task> action, Entry e)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _operations.Add(completion.Task);
        _ = Task.Run(async () =>
        {
            Exception? failure = null;
            try { await _recovery.ConfigureAwait(false); await action().ConfigureAwait(false); }
            catch (OperationCanceledException ex) when (e.InstallCancellation?.IsCancellationRequested == true || _stopping.IsCancellationRequested)
            {
                failure = ex;
                SetState(e, e.HasFiles ? PackagePhase.Failed : PackagePhase.Missing, e.HasFiles ? "修复已取消，已保留原安装。" : "");
                _log.Write("Packages", $"{e.Definition.Package.Id}：安装已取消。");
            }
            catch (Exception ex) { failure = ex; SetState(e, PackagePhase.Failed, ex.Message); _log.Write("Packages", $"{e.Definition.Package.Id} 操作失败", ex); }
            finally
            {
                lock (_gate)
                {
                    e.InstallCancellation?.Dispose(); e.InstallCancellation = null; e.Operation = null;
                    _operations.Remove(completion.Task);
                    if (failure is OperationCanceledException canceled) completion.TrySetCanceled(canceled.CancellationToken);
                    else if (failure is not null) completion.TrySetException(failure);
                    else completion.TrySetResult();
                }
                Notify();
            }
        });
        // Observe even when every waiter cancels; callers still receive the original task's failure.
        _ = completion.Task.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
        return completion.Task;
    }

    private async Task InstallCoreAsync(Entry e, PackageDefinition definition, bool replace, CancellationToken callerToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(15));
        var token = timeout.Token;
        var work = NewWorkDirectory();
        try
        {
            _ = PackageDirectory(definition);
            RecoverReplacement(e.Directory, work);
            if (!replace && Directory.Exists(e.Directory)) throw new IOException("该版本目录已存在但未通过检查，请先确认修复。");
            var zip = Path.Combine(work, "download.zip");
            using var response = await _http.GetAsync(definition.DownloadUri, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.RequestMessage?.RequestUri?.Scheme != "https") throw new InvalidDataException("下载不能重定向到非 HTTPS 地址。");
            var total = response.Content.Headers.ContentLength;
            if (total > PackageFiles.MaxDownload) throw new InvalidDataException("下载大小超过限制。");
            await using (var input = await response.Content.ReadAsStreamAsync(token))
            await using (var output = new FileStream(zip, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                byte[] buffer = new byte[81920]; long received = 0; int count; long lastNotification = 0;
                while ((count = await input.ReadAsync(buffer, token)) != 0)
                {
                    received += count;
                    if (received > PackageFiles.MaxDownload) throw new InvalidDataException("下载大小超过限制。");
                    await output.WriteAsync(buffer.AsMemory(0, count), token);
                    lock (_gate) { e.Downloaded = received; e.Total = total; }
                    // Bound UI notifications to roughly ten per second, without a polling timer.
                    if (Environment.TickCount64 - lastNotification >= 100) { Notify(); lastNotification = Environment.TickCount64; }
                }
                if (total is not null && received != total) throw new InvalidDataException("下载不完整。");
            }
            SetState(e, PackagePhase.Verifying);
            if (!string.Equals(await HashAsync(zip, token), definition.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("SHA-256 校验失败，未安装下载内容。");
            SetState(e, PackagePhase.Installing);
            var staging = Path.Combine(work, "content"); Directory.CreateDirectory(staging);
            var installedBytes = await PackageFiles.ExtractAsync(zip, staging, token);
            Dictionary<string, string> hashes = [];
            foreach (var (name, tool) in definition.Tools)
            {
                var path = PackageFiles.SafePath(staging, tool.Path);
                if (!File.Exists(path)) throw new InvalidDataException($"工具包缺少 {name}。");
                // Missing arguments retain compatibility with existing plugin declarations and installed receipts.
                var result = await _processes.RunAsync(new(path, tool.Arguments ?? ["-version"]) { Timeout = TimeSpan.FromSeconds(15), OutputLimit = 16384 }, token);
                var output = tool.Output == "stderr" ? result.StandardError : result.StandardOutput;
                var truncated = tool.Output == "stderr" ? result.StandardErrorTruncated : result.StandardOutputTruncated;
                if (result.ExitCode != 0 || truncated || !output.Replace("\r\n", "\n").StartsWith(
                    tool.VersionPrefix.Replace("\r\n", "\n"), StringComparison.Ordinal))
                    throw new InvalidDataException($"{name} 版本检查失败。");
                hashes[name] = await HashAsync(path, token);
            }
            var manifestHash = definition.Kind == PackageKind.NativeLibrary
                ? await NativePackageIntegrity.WriteAsync(staging, definition, token).ConfigureAwait(false) : null;
            await File.WriteAllTextAsync(Path.Combine(staging, "receipt.json"), JsonSerializer.Serialize(
                new Receipt(manifestHash is null ? 2 : 3, definition.Sha256, hashes, installedBytes, definition, manifestHash)), token);
            token.ThrowIfCancellationRequested();
            PackageFiles.CheckPath(e.Directory);
            PreparePackageParent(definition, work);
            if (replace && HasDirectory(e.Directory)) ReplacePackage(work, staging, e.Directory);
            else Directory.Move(staging, e.Directory);
            lock (_gate) { e.Definition = definition; e.InstalledBytes = installedBytes; e.HasFiles = true; e.OwnershipConfirmed = true; }
            SetState(e, PackagePhase.Ready);
        }
        catch (OperationCanceledException ex) when (!callerToken.IsCancellationRequested)
        { throw new TimeoutException("工具下载或验证超时，请重试。", ex); }
        finally { Cleanup(work); }
    }

    private static string ReplacementDirectory(string directory) =>
        PackageFiles.SafePath(Path.GetDirectoryName(directory)!, ".repair-" + Path.GetFileName(directory));

    private void ReplacePackage(string work, string staging, string directory)
    {
        // Publish a complete transaction before moving the installed directory. Ordinary
        // .work cleanup can then never discard the only copy of the previous installation.
        var prepared = Path.Combine(work, "replacement");
        Directory.CreateDirectory(prepared);
        File.WriteAllText(Path.Combine(prepared, "owner.txt"), RepairMarker);
        Directory.Move(staging, Path.Combine(prepared, "incoming"));
        var transaction = ReplacementDirectory(directory);
        Directory.Move(prepared, transaction);
        try
        {
            Directory.Move(directory, Path.Combine(transaction, "previous"));
            Directory.Move(Path.Combine(transaction, "incoming"), directory);
        }
        catch
        {
            RecoverReplacement(directory, work);
            throw;
        }
        try { RetireReplacement(transaction, work); }
        catch (Exception ex) { _log.Write(AppLogLevel.Warning, "Packages", "新安装已就绪，旧目录将在下次启动时清理。", ex); }
    }

    private bool RecoverReplacement(string directory, string? work = null)
    {
        var transaction = ReplacementDirectory(directory);
        if (!HasDirectory(transaction)) return false;
        if (NativePackageRetention.IsRetained(directory))
            throw new InvalidOperationException("运行库已为当前进程保留，请退出软件后再恢复安装。");
        var marker = new FileInfo(PackageFiles.SafePath(transaction, "owner.txt"));
        if (!marker.Exists || marker.Length != RepairMarker.Length || File.ReadAllText(marker.FullName) != RepairMarker)
            throw new InvalidDataException("修复暂存目录身份不明，已保留原文件。");
        var previous = PackageFiles.SafePath(transaction, "previous");
        if (HasDirectory(previous))
        {
            if (!HasDirectory(directory))
                Directory.Move(previous, directory); // Interrupted between renames. Restore before reading the receipt.
            else if (HasDirectory(PackageFiles.SafePath(transaction, "incoming")))
                throw new IOException("修复目标被其他文件占用，已保留原安装与替代包。");
        }
        RetireReplacement(transaction, work ?? NewWorkDirectory());
        return true;
    }

    private static bool HasDirectory(string directory)
    {
        PackageFiles.CheckPath(directory);
        var attributes = new FileInfo(directory).Attributes;
        if (attributes == (FileAttributes)(-1)) return false;
        if (!attributes.HasFlag(FileAttributes.Directory)) throw new IOException("工具目录被文件占用：" + directory);
        return true;
    }

    private static void RetireReplacement(string transaction, string work) =>
        Directory.Move(transaction, Path.Combine(work, Guid.NewGuid().ToString("N")));

    private string NewWorkDirectory()
    {
        var work = PackageFiles.SafePath(_root, ".work/" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        File.WriteAllText(Path.Combine(work, "owner.txt"), WorkMarker);
        return work;
    }
    private void RecoverWork()
    {
        try
        {
            var parent = PackageFiles.SafePath(_root, ".work");
            if (!Directory.Exists(parent)) return;
            // Request-side journal recovery can retire more directories while this scan is running.
            string[] candidates;
            lock (_gate) candidates = Directory.GetDirectories(parent);
            foreach (var work in candidates)
            {
                if (!Guid.TryParseExact(Path.GetFileName(work), "N", out _)) continue;
                try
                {
                    var marker = PackageFiles.SafePath(work, "owner.txt");
                    if (File.Exists(marker) && new FileInfo(marker).Length == WorkMarker.Length && File.ReadAllText(marker) == WorkMarker)
                        PackageFiles.DeleteOwnedTree(_root, work, "owner.txt");
                }
                catch (Exception ex) { _log.Write("Packages", "保留无法恢复的工具暂存目录：" + work, ex); }
            }
        }
        catch (Exception ex) { _log.Write("Packages", "检查工具暂存目录失败", ex); }
    }
    private void Cleanup(string work)
    {
        try { PackageFiles.DeleteOwnedTree(_root, work, "owner.txt"); }
        catch (Exception ex) { _log.Write("Packages", "工具临时目录清理失败，保留供排查：" + work, ex); }
    }
    private static async Task<string> HashAsync(string path, CancellationToken token)
    {
        PackageFiles.CheckPath(path);
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        return Convert.ToHexString(await SHA256.HashDataAsync(file, token)).ToLowerInvariant();
    }
    private Receipt? ReadReceipt(Entry e)
    {
        PackageFiles.CheckPath(e.Directory);
        if (!Directory.Exists(e.Directory)) return null;
        var path = PackageFiles.SafePath(e.Directory, "receipt.json");
        if (!File.Exists(path) || new FileInfo(path).Length > 65536) throw new InvalidDataException("工具安装记录缺失或过大。");
        var receipt = JsonSerializer.Deserialize<Receipt>(File.ReadAllText(path));
        if (receipt is null || receipt.Schema is not (1 or 2 or 3) || !string.Equals(receipt.ArchiveHash, e.Definition.Sha256, StringComparison.OrdinalIgnoreCase) || receipt.Tools is null ||
            receipt.Tools.Count != e.Definition.Tools.Count || receipt.InstalledBytes <= 0 || receipt.InstalledBytes > 1024L * 1024 * 1024)
            throw new InvalidDataException("工具安装记录与目录不符。");
        if ((e.Definition.Kind == PackageKind.NativeLibrary) != (receipt.Schema == 3))
            throw new InvalidDataException("依赖类型与安装记录版本不符。");
        if (receipt.Schema >= 2)
        {
            var definition = receipt.Definition ?? throw new InvalidDataException("安装记录缺少工具资料。");
            PackageDefinitions.Validate(definition);
            RequireSameArtifact(e.Definition, definition);
        }
        if (receipt.Schema == 3 && (receipt.ManifestHash is not { Length: 64 } || !receipt.ManifestHash.All(Uri.IsHexDigit)))
            throw new InvalidDataException("运行库安装记录缺少有效的载荷摘要，无法确认目录归属。");
        lock (_gate) e.OwnershipConfirmed = true;
        foreach (var (name, tool) in e.Definition.Tools)
            if (!receipt.Tools.TryGetValue(name, out var hash) || hash is null || hash.Length != 64 || !hash.All(Uri.IsHexDigit) ||
                !File.Exists(PackageFiles.SafePath(e.Directory, tool.Path))) throw new InvalidDataException("工具文件缺失或安装记录无效。");
        if (receipt.Schema == 3)
            NativePackageIntegrity.ValidateReceiptMetadata(e.Directory, receipt.ManifestHash!);
        return receipt;
    }
    private static void RequireNotRetained(Entry e)
    {
        if (NativePackageRetention.IsRetained(e.Directory))
            throw new InvalidOperationException("运行库已为当前进程保留，请退出软件后再修复或清理。");
    }
    private static bool CanManageFiles(Entry e) => e.Definition.Kind == PackageKind.Tool || !e.HasFiles || e.OwnershipConfirmed;
    private static void RequireOwnedFiles(Entry e)
    {
        if (!CanManageFiles(e))
            throw new InvalidOperationException("无法确认运行库目录归属，已保留文件。请先恢复安装记录，或备份并移走冲突目录后重启软件。");
    }
    private static void RequireSupportedKind(PackageKind kind, bool supportsNativeLibraries)
    {
        if (kind == PackageKind.NativeLibrary && !supportsNativeLibraries)
            throw new NotSupportedException("插件须声明 API 1.8 才能申请 DLL 运行库；旧版插件仅支持外部工具。");
    }
    private void Retain(Entry e, string owner)
    {
        lock (_gate)
        {
            EnsureRunning();
            if (e.Definition.Kind != PackageKind.NativeLibrary)
                throw new InvalidOperationException("仅 DLL 运行库支持保留到进程退出。");
            NativePackageRetention.Retain(e.Directory, owner);
        }
    }
    private void Release(Entry e, string owner)
    {
        lock (_gate) { if (--e.Users[owner] == 0) e.Users.Remove(owner); }
        Notify();
    }
    private void SetState(Entry e, PackagePhase phase, string error = "")
    {
        lock (_gate) { e.Phase = phase; e.Error = error; if (phase == PackagePhase.Missing) e.InstalledBytes = null; }
        if (phase == PackagePhase.Ready) _log.Write("Packages", $"{e.Definition.Package.Id} {e.Definition.Package.Version}：依赖就绪。");
        Notify();
    }
    private Entry Find(PackageReference package) => _entries.TryGetValue(package, out var e) ? e : throw new NotSupportedException("插件未提供安装资料：此工具版本或平台。");
    private void EnsureRunning() => ObjectDisposedException.ThrowIf(_shutdown is not null, this);
    private void Notify()
    {
        foreach (var handler in Changed?.GetInvocationList() ?? [])
            try { ((Action)handler)(); } catch (Exception ex) { _log.Write("Packages", "工具状态订阅失败", ex); }
    }
    public ValueTask DisposeAsync()
    {
        lock (_gate)
            return new(_shutdown ??= Task.Run(async () =>
            {
                // Begin every cancellation before waiting for any process or callback to drain.
                var stop = _stopping.CancelAsync();
                var python = Python.DisposeAsync().AsTask();
                var node = Node.DisposeAsync().AsTask();
                try { await Task.WhenAll(stop, python, node).ConfigureAwait(false); }
                catch (Exception ex) { _log.Write("Packages", "退出时结束依赖环境失败", ex); }
                finally { Python.Changed -= Notify; Node.Changed -= Notify; }
                Task[] active; lock (_gate) active = _operations.ToArray();
                try { await Task.WhenAll(active).ConfigureAwait(false); }
                catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { _log.Write("Packages", "退出时已取消工具操作。"); }
                catch (Exception ex) { _log.Write("Packages", "退出时结束工具操作失败", ex); }
                _stopping.Dispose();
            }));
    }
    private sealed class Entry(PackageDefinition definition, string directory)
    {
        public PackageDefinition Definition { get; set; } = definition;
        public string Directory { get; } = directory;
        public PackagePhase Phase;
        public string Error = "";
        public long Downloaded;
        public long? Total;
        public long? InstalledBytes;
        public Dictionary<string, int> Users { get; } = [];
        public Task? Operation;
        public CancellationTokenSource? InstallCancellation;
        public PackageDefinition? SourceDefinition;
        public bool SourceAvailable => SourceDefinition is not null;
        public bool HasFiles;
        public bool OwnershipConfirmed;
        public bool Requested;
        public int Requests;
        public Task<bool>? Preparation;
    }
    private sealed record Receipt(int Schema, string ArchiveHash, Dictionary<string, string> Tools, long InstalledBytes,
        PackageDefinition? Definition = null, string? ManifestHash = null);
    private sealed class Lease(string directory, IReadOnlyDictionary<string, string> tools,
        IReadOnlyDictionary<string, string> libraries, Action release, Action retain, Action notify) : IPackageLease
    {
        private readonly object _gate = new();
        private Action? _release = release;
        public string Directory => directory;
        public IReadOnlyDictionary<string, string> Tools => tools;
        public IReadOnlyDictionary<string, string> Libraries => libraries;
        public void RetainUntilProcessExit()
        {
            lock (_gate) { ObjectDisposedException.ThrowIf(_release is null, this); retain(); }
            notify();
        }
        public void Dispose()
        {
            Action? action;
            lock (_gate) { action = _release; _release = null; }
            action?.Invoke();
        }
    }
}
