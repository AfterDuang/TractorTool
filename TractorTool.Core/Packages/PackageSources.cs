using System.Net;
using System.Text.Json;
using TractorTool.Contracts;
using TractorTool.Contracts.Packages;
using TractorTool.Core.Storage;

namespace TractorTool.Core.Packages;

public sealed partial class ExternalPackageService
{
    private readonly string _sourcesFile;
    private DependencySource[] _sources = [new("GitHub", DependencySource.GitHubUrl)];
    private readonly Dictionary<PackageReference, Task> _resolutions = [];
    private string _sourceError = "";
    private string _settingsError = "";
    public IReadOnlyList<DependencySource> GetSources() { lock (_gate) return _sources.ToArray(); }

    private void LoadSources()
    {
        try
        {
            PackageFiles.CheckPath(_sourcesFile);
            if (File.Exists(_sourcesFile) && new FileInfo(_sourcesFile).Length > 32768)
                throw new InvalidDataException("依赖来源配置过大。");
            var settings = new JsonFileStore<SourceSettings>(_sourcesFile, ValidateSources).Load();
            _sources = [new("GitHub", DependencySource.GitHubUrl), .. settings.Sources];
        }
        catch (Exception ex)
        {
            _settingsError = "依赖来源配置不可用，原文件已保留：" + ex.Message;
            _log.Write(AppLogLevel.Warning, "Packages", _settingsError, ex);
        }
    }

    public void AddSource(string name, string address)
    {
        var source = new DependencySource(name.Trim(), DependencySource.ApiUri(address.Trim()).AbsoluteUri);
        lock (_gate)
        {
            EnsureRunning();
            SaveSources([.. _sources.Where(s => !s.IsDefault), source]);
        }
        _log.Write("Packages", $"已添加 GitHub 依赖来源：{source.Name}。"); Notify();
    }
    public void RemoveSource(DependencySource source)
    {
        lock (_gate)
        {
            EnsureRunning();
            if (source.IsDefault) throw new InvalidOperationException("默认 GitHub 来源不能删除。");
            SaveSources(_sources.Where(s => !s.IsDefault && s.Url != source.Url).ToArray());
        }
        _log.Write("Packages", $"已移除依赖来源：{source.Name}；保留已安装包。"); Notify();
    }
    private void SaveSources(DependencySource[] sources)
    {
        if (_settingsError.Length != 0) throw new InvalidOperationException(_settingsError);
        PackageFiles.CheckPath(_sourcesFile);
        // ponytail: tiny, infrequent settings writes share the service lock; split if write latency becomes measurable.
        new JsonFileStore<SourceSettings>(_sourcesFile, ValidateSources).Save(new() { Sources = sources });
        _sources = [new("GitHub", DependencySource.GitHubUrl), .. sources];
    }
    private static void ValidateSources(SourceSettings settings)
    {
        if (settings.Sources is null || settings.Sources.Length > 32) throw new InvalidDataException("依赖来源数量无效。");
        var urls = new HashSet<string>(StringComparer.Ordinal) { DependencySource.GitHubUrl };
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "GitHub" };
        foreach (var source in settings.Sources)
            if (source is null || string.IsNullOrWhiteSpace(source.Name) || source.Name.Length > 80 || source.Name.Any(char.IsControl) ||
                !urls.Add(DependencySource.ApiUri(source.Url).AbsoluteUri) || !names.Add(source.Name))
                throw new InvalidDataException("依赖来源名称或地址无效、重复。");
    }
    private sealed class SourceSettings : IVersionedSettings
    {
        public int SchemaVersion { get; init; } = 1;
        public DependencySource[] Sources { get; init; } = [];
    }

    private bool HasDownload(PackageReference package) => _references.Dependencies.Any(d => d.Package == package &&
        d.Downloads?.Any(p => p.Rid == _rid) == true);

    private GitHubPackage? DeclaredDownload(PackageReference package, PackageDefinition? installed = null)
    {
        var candidates = _references.Dependencies.Where(d => d.Package == package)
            .SelectMany(d => d.Downloads ?? []).Where(p => p.Rid == _rid).ToArray();
        if (candidates.Length == 0) return null;
        var recipe = candidates[0]; var definition = recipe.Definition(package);
        if (_pendingReceipts.TryGetValue(package, out var pending) && pending?.ArchiveHash is { Length: 64 } hash &&
            hash.All(Uri.IsHexDigit) && !string.Equals(hash, definition.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{package.Id} {package.Version} 的下载声明与原安装校验值冲突，请插件作者使用新版本或独立标识。");
        foreach (var candidate in candidates.Skip(1)) RequireSameArtifact(definition, candidate.Definition(package));
        if (installed is not null) RequireSameArtifact(installed, definition);
        return recipe;
    }

    private Task EnsureSourceAsync(PackageReference package, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (_gate)
        {
            EnsureRunning();
            if (HasConflictingIdentity(package))
                throw new InvalidDataException("依赖标识或版本的大小写与已有记录冲突，请插件作者统一拼写。");
            _entries.TryGetValue(package, out var e);
            var recipe = DeclaredDownload(package, e?.Definition);
            if (e is not null && (e.Phase == PackagePhase.Ready || e.Preparation is not null ||
                e.Operation is not null || !HasDownload(package) && e.SourceAvailable)) return Task.CompletedTask;
            if (!_resolutions.TryGetValue(package, out var task))
            {
                if (recipe is null)
                    throw new NotSupportedException($"{package.Id} {package.Version} 尚无 {_rid} 的 GitHub 安装资料，请更新或联系插件作者。");
                if (!_sources.Any(s => DependencySource.ApiUri(s.Url).AbsoluteUri == recipe.Source))
                    throw new InvalidOperationException($"请先在依赖来源中添加 {recipe.Source}。");
                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                task = completion.Task; _resolutions.Add(package, task); _operations.Add(task);
                _ = task.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
                _ = Task.Run(async () =>
                {
                    Exception? failure = null;
                    try
                    {
                        var definition = await ResolveGitHubAsync(package, recipe, _stopping.Token).ConfigureAwait(false);
                        lock (_gate)
                        {
                            EnsureRunning();
                            _ = DeclaredDownload(package, definition);
                            if (_entries.TryGetValue(package, out var previous))
                            {
                                RequireSameArtifact(previous.Definition, definition);
                                previous.SourceDefinition = definition;
                                if (!previous.HasFiles && previous.Operation is null && previous.Preparation is null) previous.Definition = definition;
                            }
                            else _entries.Add(package, new Entry(definition, Path.Combine(_root, package.Id, package.Version, _rid))
                                { SourceDefinition = definition });
                        }
                    }
                    catch (Exception ex) { failure = ex; }
                    finally
                    {
                        lock (_gate)
                        {
                            _resolutions.Remove(package); _operations.Remove(completion.Task);
                            if (failure is OperationCanceledException canceled) completion.TrySetCanceled(canceled.CancellationToken);
                            else if (failure is not null) completion.TrySetException(failure);
                            else completion.TrySetResult();
                        }
                        Notify();
                    }
                });
            }
            return task.WaitAsync(token);
        }
    }

    private async Task<PackageDefinition> ResolveGitHubAsync(PackageReference package, GitHubPackage recipe, CancellationToken stopping)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var token = timeout.Token;
        try
        {
            var endpoint = new Uri(DependencySource.ApiUri(recipe.Source), $"repos/{recipe.Repository}/releases/tags/{Uri.EscapeDataString(recipe.Tag)}");
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            request.Headers.UserAgent.ParseAdd("TractorTool/0.1");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound) throw new InvalidDataException($"GitHub 未找到 {recipe.Repository} 的 Release {recipe.Tag}；标签必须发布 ZIP 附件。");
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                throw new HttpRequestException("GitHub 拒绝访问或请求额度已用完，请稍后重试。", null, response.StatusCode);
            response.EnsureSuccessStatusCode();
            if (response.RequestMessage?.RequestUri?.GetLeftPart(UriPartial.Authority) != endpoint.GetLeftPart(UriPartial.Authority))
                throw new InvalidDataException("GitHub API 不能重定向至其他站点或协议。");
            const int maxBytes = 2 * 1024 * 1024;
            if (response.Content.Headers.ContentLength > maxBytes) throw new InvalidDataException("GitHub Release 资料过大。");
            await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var content = new MemoryStream();
            var buffer = new byte[16384]; int count;
            while ((count = await input.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
            {
                if (content.Length + count > maxBytes) throw new InvalidDataException("GitHub Release 资料过大。");
                content.Write(buffer, 0, count);
            }
            using var json = JsonDocument.Parse(content.ToArray());
            var release = json.RootElement;
            if (release.GetProperty("tag_name").GetString() != recipe.Tag || release.GetProperty("draft").GetBoolean())
                throw new InvalidDataException("GitHub Release 标签不匹配或尚未发布。");
            var assets = release.GetProperty("assets").EnumerateArray().Where(a => a.GetProperty("name").GetString() == recipe.Asset).ToArray();
            if (assets.Length != 1) throw new InvalidDataException($"GitHub Release 未提供唯一附件 {recipe.Asset}。");
            var asset = assets[0];
            var definition = recipe.Definition(package);
            if (asset.GetProperty("state").GetString() != "uploaded" || asset.GetProperty("size").GetInt64() is <= 0 or > PackageFiles.MaxDownload ||
                !Uri.TryCreate(asset.GetProperty("browser_download_url").GetString(), UriKind.Absolute, out var download) || download != definition.DownloadUri)
                throw new InvalidDataException("GitHub 附件地址、状态或大小与声明不符。");
            if (asset.TryGetProperty("digest", out var digest) && digest.ValueKind == JsonValueKind.String &&
                !string.Equals(digest.GetString(), "sha256:" + recipe.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("GitHub 附件校验值与插件声明不符。");
            return PackageDefinitions.Snapshot(definition);
        }
        catch (OperationCanceledException ex) when (!stopping.IsCancellationRequested)
        { throw new TimeoutException("查询 GitHub Release 超时，请重试。", ex); }
    }

    private static void RequireSameArtifact(PackageDefinition previous, PackageDefinition next)
    {
        // Legacy receipts may point to an upstream mirror; identical bytes and tool contracts can be reused.
        if (previous.Package != next.Package || previous.Rid != next.Rid ||
            !string.Equals(previous.Sha256, next.Sha256, StringComparison.OrdinalIgnoreCase) ||
            previous.Tools.Count != next.Tools.Count || previous.Tools.Any(p => !next.Tools.TryGetValue(p.Key, out var tool) ||
                p.Value.Path != tool.Path || p.Value.VersionPrefix != tool.VersionPrefix || p.Value.Output != tool.Output ||
                !(p.Value.Arguments ?? ["-version"]).SequenceEqual(tool.Arguments ?? ["-version"])))
            throw new InvalidDataException($"{previous.Package.Id} {previous.Package.Version} 的下载声明冲突，请插件作者使用新的包版本或独立标识。");
    }
}
