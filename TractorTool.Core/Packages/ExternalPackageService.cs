using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text.Json;
using TractorTool.Contracts;
using TractorTool.Contracts.Packages;
using TractorTool.Contracts.Processes;
using TractorTool.Core.Storage;

namespace TractorTool.Core.Packages;

/// <summary>One host-owned service per data root. The host holds its data-root instance lock.</summary>
public sealed class ExternalPackageService : IAsyncDisposable
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
    public event Action? Changed;
    public IReadOnlyList<PackageDefinition> Definitions { get; }

    public ExternalPackageService(string root, string rid, HttpClient http, IProcessRunner processes, IAppLog log,
        IEnumerable<PackageDefinition>? definitions = null)
    {
        _root = AppPaths.Absolute(root); PackageFiles.CheckPath(_root);
        _http = http; _processes = processes; _log = log;
        Definitions = Array.AsReadOnly((definitions ?? PackageCatalog.All).Where(d => d.Rid == rid)
            .Select(d => d with { Tools = new ReadOnlyDictionary<string, PackageTool>(new Dictionary<string, PackageTool>(d.Tools)) }).ToArray());
        _entries = [];
        foreach (var d in Definitions)
        {
            AppPaths.ValidateId(d.Package.Id); AppPaths.ValidateId(d.Package.Version); AppPaths.ValidateId(d.Rid);
            if (d.DownloadUri.Scheme != "https" || d.Sha256.Length != 64 || !d.Sha256.All(Uri.IsHexDigit) || d.Tools.Count == 0)
                throw new InvalidDataException("外部工具目录元数据无效。");
            var entry = new Entry(d, Path.Combine(_root, d.Package.Id, d.Package.Version, d.Rid));
            if (!_entries.TryAdd(d.Package, entry)) throw new InvalidDataException("外部工具目录重复。");
            try
            {
                foreach (var tool in d.Tools.Values) _ = PackageFiles.SafePath(entry.Directory, tool.Path);
                var receipt = ReadReceipt(entry);
                entry.InstalledBytes = receipt?.InstalledBytes;
                entry.Phase = receipt is null ? PackagePhase.Missing : PackagePhase.Ready;
            }
            catch (Exception ex) { entry.Phase = PackagePhase.Failed; entry.Error = ex.Message; }
        }
        _recovery = Task.Run(RecoverWork);
        _operations.Add(_recovery);
    }

    public PackageStatus GetStatus(PackageReference package)
    {
        lock (_gate)
        {
            var e = Find(package);
            return new(package, e.Definition.Rid, e.Phase, e.Downloaded, e.Total, e.Error,
                Array.AsReadOnly(e.Users.Keys.Order().ToArray()), e.InstallCancellation is not null, e.Directory, e.InstalledBytes);
        }
    }

    // Caller cancellation only cancels waiting. Explicit CancelInstall cancels the shared transfer for all waiters.
    public Task InstallAsync(PackageReference package, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task task;
        lock (_gate)
        {
            EnsureRunning(); var e = Find(package);
            if (e.Phase == PackagePhase.Ready && e.Operation is null) return Task.CompletedTask;
            if (e.Operation is null)
            {
                if (e.Users.Count != 0) throw new InvalidOperationException("工具仍被使用，不能重新安装。");
                e.InstallCancellation = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
                e.Phase = PackagePhase.Downloading; e.Error = ""; e.Downloaded = 0; e.Total = null;
                e.Operation = StartOperation(() => InstallCoreAsync(e, e.InstallCancellation.Token), e);
            }
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
            if (e.Operation is not null || e.Users.Count != 0) throw new InvalidOperationException("工具正在安装或使用中，暂时不能移除。");
            e.Phase = PackagePhase.Installing; e.Error = "";
            task = e.Operation = StartOperation(() =>
            {
                PackageFiles.CheckPath(e.Directory);
                if (Directory.Exists(e.Directory))
                {
                    // Detach atomically before deleting, so a partial delete can never remain Ready.
                    var trash = NewWorkDirectory();
                    var detached = Path.Combine(trash, "removed");
                    try { Directory.Move(e.Directory, detached); PackageFiles.DeleteOwnedTree(_root, trash, "owner.txt"); }
                    catch { Cleanup(trash); throw; }
                }
                SetState(e, PackagePhase.Missing);
                return Task.CompletedTask;
            }, e);
        }
        Notify(); return task;
    }

    internal Task<IPackageLease> AcquireAsync(PackageReference package, string owner, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (_gate)
        {
            EnsureRunning(); var e = Find(package);
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
                        if (await HashAsync(path, linked.Token) != receipt.Tools[name]) throw new InvalidDataException("工具文件校验失败，请移除后重新安装。");
                    }
                    linked.Token.ThrowIfCancellationRequested();
                    completion.TrySetResult(new Lease(e.Directory, new ReadOnlyDictionary<string, string>(e.Definition.Tools.ToDictionary(
                        p => p.Key, p => PackageFiles.SafePath(e.Directory, p.Value.Path))), () => Release(e, owner)));
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

    private Task StartOperation(Func<Task> action, Entry e)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _operations.Add(completion.Task);
        _ = Task.Run(async () =>
        {
            Exception? failure = null;
            try { await _recovery.ConfigureAwait(false); await action().ConfigureAwait(false); }
            catch (OperationCanceledException ex) when (e.InstallCancellation?.IsCancellationRequested == true || _stopping.IsCancellationRequested)
            { failure = ex; SetState(e, PackagePhase.Missing); }
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

    private async Task InstallCoreAsync(Entry e, CancellationToken callerToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(15));
        var token = timeout.Token;
        if (Directory.Exists(e.Directory)) throw new IOException("该版本目录已存在但未通过检查，请先移除再重试。");
        var work = NewWorkDirectory();
        try
        {
            var zip = Path.Combine(work, "download.zip");
            using var response = await _http.GetAsync(e.Definition.DownloadUri, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
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
            if (!string.Equals(await HashAsync(zip, token), e.Definition.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("SHA-256 校验失败，未安装下载内容。");
            SetState(e, PackagePhase.Installing);
            var staging = Path.Combine(work, "content"); Directory.CreateDirectory(staging);
            var installedBytes = await PackageFiles.ExtractAsync(zip, staging, token);
            Dictionary<string, string> hashes = [];
            foreach (var (name, tool) in e.Definition.Tools)
            {
                var path = PackageFiles.SafePath(staging, tool.Path);
                if (!File.Exists(path)) throw new InvalidDataException($"工具包缺少 {name}。");
                var result = await _processes.RunAsync(new(path, ["-version"]) { Timeout = TimeSpan.FromSeconds(15), OutputLimit = 16384 }, token);
                if (result.ExitCode != 0 || result.StandardOutputTruncated || !result.StandardOutput.StartsWith(tool.VersionPrefix, StringComparison.Ordinal))
                    throw new InvalidDataException($"{name} 版本检查失败。");
                hashes[name] = await HashAsync(path, token);
            }
            await File.WriteAllTextAsync(Path.Combine(staging, "receipt.json"), JsonSerializer.Serialize(
                new Receipt(1, e.Definition.Sha256, hashes, installedBytes)), token);
            token.ThrowIfCancellationRequested();
            PackageFiles.CheckPath(e.Directory);
            Directory.CreateDirectory(Path.GetDirectoryName(e.Directory)!);
            Directory.Move(staging, e.Directory);
            lock (_gate) e.InstalledBytes = installedBytes;
            SetState(e, PackagePhase.Ready);
        }
        catch (OperationCanceledException ex) when (!callerToken.IsCancellationRequested)
        { throw new TimeoutException("工具下载或验证超时，请重试。", ex); }
        finally { Cleanup(work); }
    }

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
            foreach (var work in Directory.EnumerateDirectories(parent))
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
    private static Receipt? ReadReceipt(Entry e)
    {
        PackageFiles.CheckPath(e.Directory);
        if (!Directory.Exists(e.Directory)) return null;
        var path = PackageFiles.SafePath(e.Directory, "receipt.json");
        if (!File.Exists(path) || new FileInfo(path).Length > 65536) throw new InvalidDataException("工具安装记录缺失或过大。");
        var receipt = JsonSerializer.Deserialize<Receipt>(File.ReadAllText(path));
        if (receipt is null || receipt.Schema != 1 || receipt.ArchiveHash != e.Definition.Sha256 || receipt.Tools is null ||
            receipt.Tools.Count != e.Definition.Tools.Count || receipt.InstalledBytes <= 0 || receipt.InstalledBytes > 1024L * 1024 * 1024)
            throw new InvalidDataException("工具安装记录与目录不符。");
        foreach (var (name, tool) in e.Definition.Tools)
            if (!receipt.Tools.TryGetValue(name, out var hash) || hash is null || hash.Length != 64 || !hash.All(Uri.IsHexDigit) ||
                !File.Exists(PackageFiles.SafePath(e.Directory, tool.Path))) throw new InvalidDataException("工具文件缺失或安装记录无效。");
        return receipt;
    }
    private void Release(Entry e, string owner)
    {
        lock (_gate) { if (--e.Users[owner] == 0) e.Users.Remove(owner); }
        Notify();
    }
    private void SetState(Entry e, PackagePhase phase, string error = "")
    {
        lock (_gate) { e.Phase = phase; e.Error = error; if (phase == PackagePhase.Missing) e.InstalledBytes = null; }
        Notify();
    }
    private Entry Find(PackageReference package) => _entries.TryGetValue(package, out var e) ? e : throw new NotSupportedException("宿主目录不支持此工具版本或平台。");
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
                await _stopping.CancelAsync();
                Task[] active; lock (_gate) active = _operations.ToArray();
                try { await Task.WhenAll(active).ConfigureAwait(false); }
                catch (Exception ex) { _log.Write("Packages", "退出时结束工具操作", ex); }
                _stopping.Dispose();
            }));
    }
    private sealed class Entry(PackageDefinition definition, string directory)
    {
        public PackageDefinition Definition { get; } = definition;
        public string Directory { get; } = directory;
        public PackagePhase Phase;
        public string Error = "";
        public long Downloaded;
        public long? Total;
        public long? InstalledBytes;
        public Dictionary<string, int> Users { get; } = [];
        public Task? Operation;
        public CancellationTokenSource? InstallCancellation;
    }
    private sealed record Receipt(int Schema, string ArchiveHash, Dictionary<string, string> Tools, long InstalledBytes);
    private sealed class Lease(string directory, IReadOnlyDictionary<string, string> tools, Action release) : IPackageLease
    {
        private Action? _release = release;
        public string Directory => directory;
        public IReadOnlyDictionary<string, string> Tools => tools;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}
