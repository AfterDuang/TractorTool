using System.Text.Json;
using TractorTool.Contracts;
using TractorTool.Contracts.Packages;

namespace TractorTool.Core.Packages;

public sealed partial class ExternalPackageService
{
    private readonly Uri? _sourceUri;
    private readonly string? _sourceFile;
    private readonly string? _sourceCacheFile;
    private IReadOnlyList<PackageDefinition> _bundledSource;
    private Task<bool>? _sourceRefresh;
    private string _sourceStatus = "安装源：本地目录 · 远程更新尚未配置";
    private string _refreshError = "";
    private string _sourceError = "";
    public bool HasRemoteSource => _sourceUri is not null;
    public string SourceLocation => _sourceUri?.AbsoluteUri ?? _sourceFile ?? "软件提供的内存目录";
    public bool CanRefreshSource { get { lock (_gate) return (_sourceUri is not null || _sourceFile is not null) && _shutdown is null; } }
    public bool IsRefreshingSource { get { lock (_gate) return _sourceRefresh is not null; } }
    public string SourceStatus { get { lock (_gate) return _sourceRefresh is null ? _sourceStatus : "正在更新安装源…"; } }

    // Only the software distributor supplies this file; plugins cannot choose an index URL.
    private Uri? LoadSourceUri()
    {
        try
        {
            var file = Path.Combine(Path.GetDirectoryName(_sourceFile!)!, "source.json");
            PackageFiles.CheckPath(file);
            if (!File.Exists(file)) return null;
            if (new FileInfo(file).Length > 4096) throw new InvalidDataException("安装源配置过大。");
            var settings = JsonSerializer.Deserialize<SourceSettings>(File.ReadAllText(file), new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (settings is null || settings.SchemaVersion != 1) throw new InvalidDataException("安装源配置格式不支持。");
            if (settings.IndexUri is { } uri) ValidateSourceUri(uri);
            return settings.IndexUri;
        }
        catch (Exception ex)
        {
            SourceError = "安装源配置不可用，使用内置目录：" + ex.Message;
            _log.Write(AppLogLevel.Warning, "Packages", SourceError, ex);
            return null;
        }
    }

    private static void ValidateSourceUri(Uri uri)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme != "https" || uri.Host.Length == 0 || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
            throw new InvalidDataException("安装源必须为不含凭据或片段的 HTTPS 地址。");
    }

    private IReadOnlyList<PackageDefinition> LoadInitialSource()
    {
        if (_sourceUri is null) return MergeSources([]);
        _sourceStatus = $"安装源：{_sourceUri.Host} · 按需更新";
        try
        {
            PackageFiles.CheckPath(_sourceCacheFile!);
            if (!File.Exists(_sourceCacheFile)) return MergeSources([]);
            var source = MergeSources(PackageCatalog.Load(_sourceCacheFile!));
            _sourceStatus = $"安装源：{_sourceUri.Host} · 使用本地缓存";
            return source;
        }
        catch (Exception ex)
        {
            _refreshError = "安装源缓存不可用，使用内置目录：" + ex.Message;
            _log.Write(AppLogLevel.Warning, "Packages", _refreshError, ex);
            return MergeSources([]);
        }
    }

    private IReadOnlyList<PackageDefinition> MergeSources(IReadOnlyList<PackageDefinition> remote,
        IReadOnlyList<PackageDefinition>? bundled = null)
    {
        var merged = new Dictionary<string, PackageDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in (bundled ?? _bundledSource).Concat(remote))
        {
            var key = $"{definition.Package.Id}/{definition.Package.Version}/{definition.Rid}";
            if (merged.TryGetValue(key, out var previous)) RequireSameArtifact(previous, definition);
            merged[key] = definition;
        }
        if (merged.Count > 1000) throw new InvalidDataException("安装源合并后超过包数量限制。");
        return merged.Values.ToArray();
    }

    private static void RequireSameArtifact(PackageDefinition previous, PackageDefinition next)
    {
        if (previous.Package != next.Package || previous.Rid != next.Rid ||
            !string.Equals(previous.Sha256, next.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"安装源改变了 {previous.Package.Id} {previous.Package.Version} 的身份或校验值，请发布新的包版本。");
    }

    private async Task EnsureSourceAsync(PackageReference package, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (_gate)
        {
            EnsureRunning();
            if (!CanRefreshSource || _entries.TryGetValue(package, out var entry) &&
                (entry.SourceAvailable || entry.Phase == PackagePhase.Ready || entry.Preparation is not null)) return;
        }
        await RefreshSourceAsync(token).ConfigureAwait(false);
    }

    // All requesters share one transfer. A caller may stop waiting without cancelling other callers.
    public Task<bool> RefreshSourceAsync(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        Task<bool> task;
        lock (_gate)
        {
            EnsureRunning();
            if (!CanRefreshSource) return Task.FromResult(false);
            task = _sourceRefresh ??= StartSourceRefresh();
        }
        Notify();
        return task.WaitAsync(token);
    }

    private Task<bool> StartSourceRefresh()
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _operations.Add(completion.Task);
        _refreshError = "";
        _ = Task.Run(async () =>
        {
            var succeeded = false;
            string? temporary = null;
            try
            {
                var bundled = _sourceFile is null ? _bundledSource : PackageCatalog.Load(_sourceFile);
                if (_sourceUri is null)
                {
                    var local = MergeSources([], bundled);
                    lock (_gate)
                    {
                        EnsureRunning();
                        _stopping.Token.ThrowIfCancellationRequested();
                        ValidateSourceUpdate(local);
                        ApplySourceUpdate(local);
                        _bundledSource = bundled;
                        _sourceStatus = "安装源：本地目录 · 已重新读取（未进行远程更新）";
                        succeeded = true;
                    }
                    _log.Write("Packages", "本地安装目录已重新读取。");
                    return;
                }
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                var token = timeout.Token;
                using var response = await _http.GetAsync(_sourceUri, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                var actual = response.RequestMessage?.RequestUri;
                if (actual is null || actual.GetLeftPart(UriPartial.Authority) != _sourceUri!.GetLeftPart(UriPartial.Authority))
                    throw new InvalidDataException("安装源不能重定向到其他站点或协议。");
                var length = response.Content.Headers.ContentLength;
                if (length > PackageCatalog.MaxBytes) throw new InvalidDataException("依赖源索引超过大小限制。");
                await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                using var content = new MemoryStream();
                var buffer = new byte[16384];
                int count;
                while ((count = await input.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
                {
                    if (content.Length + count > PackageCatalog.MaxBytes) throw new InvalidDataException("依赖源索引超过大小限制。");
                    content.Write(buffer, 0, count);
                }
                if (length is not null && length != content.Length) throw new InvalidDataException("依赖源索引下载不完整。");
                var bytes = content.ToArray();
                var source = MergeSources(PackageCatalog.Parse(bytes), bundled);
                lock (_gate) ValidateSourceUpdate(source);

                PackageFiles.CheckPath(_sourceCacheFile!);
                Directory.CreateDirectory(Path.GetDirectoryName(_sourceCacheFile!)!);
                temporary = _sourceCacheFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
                await File.WriteAllBytesAsync(temporary, bytes, token).ConfigureAwait(false);
                lock (_gate)
                {
                    EnsureRunning();
                    token.ThrowIfCancellationRequested();
                    ValidateSourceUpdate(source);
                    PackageFiles.CheckPath(_sourceCacheFile!);
                    File.Move(temporary, _sourceCacheFile!, true);
                    ApplySourceUpdate(source);
                    _bundledSource = bundled;
                    _sourceStatus = $"安装源：{_sourceUri!.Host} · 已更新";
                    _refreshError = "";
                    succeeded = true;
                }
                _log.Write("Packages", "可信安装源已更新。");
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { }
            catch (Exception ex)
            {
                lock (_gate) _refreshError = "安装源更新失败，保留当前目录和已安装包：" +
                    (ex is OperationCanceledException ? "更新超时，请重试。" : ex.Message);
                _log.Write(AppLogLevel.Warning, "Packages", _refreshError, ex);
            }
            finally
            {
                if (temporary is not null)
                {
                    try { PackageFiles.CheckPath(temporary); if (File.Exists(temporary)) File.Delete(temporary); }
                    catch (Exception ex) { _log.Write(AppLogLevel.Warning, "Packages", "安装源临时文件保留供排查。", ex); }
                }
                lock (_gate)
                {
                    _sourceRefresh = null;
                    _operations.Remove(completion.Task);
                    if (_stopping.IsCancellationRequested) completion.TrySetCanceled(_stopping.Token);
                    else completion.TrySetResult(succeeded);
                }
                Notify();
            }
        });
        return completion.Task;
    }

    private void ValidateSourceUpdate(IReadOnlyList<PackageDefinition> source)
    {
        var previous = _entries.Values.ToDictionary(e => $"{e.Definition.Package.Id}/{e.Definition.Package.Version}",
            e => e.Definition, StringComparer.OrdinalIgnoreCase);
        foreach (var definition in source.Where(d => d.Rid == _rid))
        {
            if (previous.TryGetValue($"{definition.Package.Id}/{definition.Package.Version}", out var old))
                RequireSameArtifact(old, definition);
        }
    }

    private void ApplySourceUpdate(IReadOnlyList<PackageDefinition> source)
    {
        var current = source.Where(d => d.Rid == _rid).ToDictionary(d => d.Package);
        foreach (var entry in _entries.Values)
        {
            entry.SourceDefinition = current.GetValueOrDefault(entry.Definition.Package);
            // Receipts and work in progress own their definition, independent of source metadata refreshes.
            if (!entry.HasFiles && entry.Operation is null && entry.Preparation is null && entry.Requests == 0 &&
                entry.Users.Count == 0 && entry.SourceDefinition is { } definition) entry.Definition = definition;
        }
        foreach (var definition in current.Values)
            _entries.TryAdd(definition.Package, new Entry(definition,
                Path.Combine(_root, definition.Package.Id, definition.Package.Version, definition.Rid)) { SourceDefinition = definition });
        foreach (var key in _entries.Where(p => !p.Value.SourceAvailable && !p.Value.HasFiles && !p.Value.Requested &&
                     p.Value.Operation is null && p.Value.Preparation is null && p.Value.Requests == 0 && p.Value.Users.Count == 0)
                     .Select(p => p.Key).ToArray()) _entries.Remove(key);
    }

    private sealed record SourceSettings(int SchemaVersion, Uri? IndexUri);
}
