using System.Collections;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using TractorTool.Contracts.Processes;

namespace TractorTool.Core.Packages.Node;

internal sealed class NodeEnvironmentInstaller(HttpClient http, IProcessRunner processes, string cache)
{
    internal async Task<NodeEnvironmentStorage.EnvironmentRecord> InstallAsync(string directory,
        NodeEnvironmentDefinition definition, Action<string> phase, CancellationToken token)
    {
        NodeEnvironmentDefinitions.Validate(definition);
        var constraints = NodeInputPolicy.Validate(definition);
        var generation = NodeEnvironmentStorage.NewGeneration(directory, definition.Identity);
        try
        {
            NodeCacheStorage.Ensure(cache);
            var project = Path.Combine(generation, "environment"); Directory.CreateDirectory(project);
            var projectFile = Path.Combine(project, "package.json"); var lockFile = Path.Combine(project, "pnpm-lock.yaml");
            await File.WriteAllTextAsync(projectFile, definition.ProjectText, NodeEnvironmentDefinitions.Utf8, token);
            await File.WriteAllTextAsync(lockFile, definition.LockText, NodeEnvironmentDefinitions.Utf8, token);
            phase("正在准备 Node.js…");
            var nodeArchive = Path.Combine(generation, "node.zip");
            await DownloadAsync($"https://nodejs.org/dist/v{definition.NodeVersion}/node-v{definition.NodeVersion}-win-x64.zip",
                nodeArchive, NodeEnvironmentDefinitions.NodeArchiveSha256, token).ConfigureAwait(false);
            await PackageFiles.ExtractAsync(nodeArchive, Path.Combine(generation, "node"), token).ConfigureAwait(false);
            File.Delete(nodeArchive);
            var node = NodeEnvironmentDefinitions.NodeExecutable(generation, definition); PackageFiles.CheckPath(node);
            phase("正在准备 pnpm…");
            var pnpmArchive = Path.Combine(generation, "pnpm.tgz");
            await DownloadAsync($"https://registry.npmjs.org/pnpm/-/pnpm-{definition.PnpmVersion}.tgz",
                pnpmArchive, NodeEnvironmentDefinitions.PnpmArchiveSha256, token).ConfigureAwait(false);
            await ExtractPnpmAsync(pnpmArchive, Path.Combine(generation, "pnpm"), token).ConfigureAwait(false);
            File.Delete(pnpmArchive);
            var pnpm = Path.Combine(generation, "pnpm", "package", "bin", "pnpm.cjs"); PackageFiles.CheckPath(pnpm);
            using (var metadata = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(generation, "pnpm", "package", "package.json"), token)))
                if (metadata.RootElement.GetProperty("name").GetString() != "pnpm" ||
                    metadata.RootElement.GetProperty("version").GetString() != definition.PnpmVersion)
                    throw new InvalidDataException("pnpm 版本与固定工具链不符。");
            var config = Path.Combine(generation, "config"); Directory.CreateDirectory(config);
            foreach (var name in new[] { "user.npmrc", "global.npmrc" }) await File.WriteAllTextAsync(Path.Combine(config, name), "", token);
            Directory.CreateDirectory(Path.Combine(config, "temp"));
            var environment = InstallEnvironment(generation, node);
            async Task<ProcessResult> RunNode(IReadOnlyList<string> arguments) => await RunCheckedAsync(new(node,
                ["--no-global-search-paths", .. arguments]) { WorkingDirectory = project, EnvironmentVariables = environment,
                Timeout = TimeSpan.FromMinutes(15) }, token).ConfigureAwait(false);
            var version = await RunNode(["--version"]);
            if (version.StandardOutput.Trim() != "v" + definition.NodeVersion) throw new InvalidDataException("Node.js 版本与固定工具链不符。");
            var semver = Path.Combine(Path.GetDirectoryName(node)!, "node_modules", "npm", "node_modules", "semver");
            PackageFiles.CheckPath(semver);
            var constraintFile = Path.Combine(config, "version-constraints.json");
            await File.WriteAllTextAsync(constraintFile, constraints, NodeEnvironmentDefinitions.Utf8, token);
            await RunNode(["--input-type=commonjs", "-e", ValidateVersionsScript, semver, constraintFile]);
            var options = PnpmOptions(generation, cache, definition.Source);
            phase("正在按锁安装 Node 依赖…");
            await RunNode([pnpm, "install", "--frozen-lockfile", "--prod", "--ignore-scripts", "--ignore-pnpmfile",
                "--reporter=append-only", "--use-stderr", "--loglevel=error", .. options]);
            if (!(await File.ReadAllBytesAsync(projectFile, token)).AsSpan().SequenceEqual(NodeEnvironmentDefinitions.Utf8.GetBytes(definition.ProjectText)) ||
                !(await File.ReadAllBytesAsync(lockFile, token)).AsSpan().SequenceEqual(NodeEnvironmentDefinitions.Utf8.GetBytes(definition.LockText)))
                throw new InvalidDataException("pnpm 修改了冻结输入，未发布环境。");
            phase("正在校验环境文件…");
            // Hoisted + copy keeps installed packages independent from the store. The inventory rejects all links.
            var (hash, bytes) = await NodeEnvironmentStorage.WriteManifestAsync(generation, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return new() { Definition = definition, Generation = Path.GetFileName(generation), InstalledRoot = generation,
                ManifestHash = hash, InstalledBytes = bytes, GenerationHashes = new() { [Path.GetFileName(generation)] = hash } };
        }
        catch
        {
            try
            {
                NodeEnvironmentStorage.CheckGeneration(generation, definition.Identity);
                PackageFiles.DeleteOwnedTree(directory, generation, NodeEnvironmentStorage.OwnerFile);
                NodeEnvironmentStorage.DeleteGenerationManifest(generation);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    internal static string[] PnpmOptions(string generation, string cache, string source) =>
    [
        "--ignore-workspace", "--registry=" + source, "--config.@jsr:registry=" + source,
        "--config.node-linker=hoisted", "--config.package-import-method=copy",
        "--config.manage-package-manager-versions=false", "--config.package-manager-strict-version=true",
        "--config.enable-global-virtual-store=false", "--config.side-effects-cache=false",
        "--config.recursive-install=false", "--config.verify-store-integrity=true", "--config.strict-store-pkg-content-check=true",
        "--config.engine-strict=true", "--config.update-notifier=false",
        "--config.userconfig=" + Path.Combine(generation, "config", "user.npmrc"),
        "--config.globalconfig=" + Path.Combine(generation, "config", "global.npmrc"),
        "--config.store-dir=" + Path.Combine(cache, "store"), "--config.cache-dir=" + Path.Combine(cache, "metadata"),
        "--config.state-dir=" + Path.Combine(generation, "config", "state")
    ];

    // The npm-shipped node-semver is covered by the fixed Node archive hash; no plugin code executes here.
    internal const string ValidateVersionsScript = """
        const semver = require(process.argv[1]);
        const input = JSON.parse(require('node:fs').readFileSync(process.argv[2], 'utf8'));
        if (input.versions.some(v => semver.valid(v) === null) ||
            input.ranges.some(r => semver.validRange(r) === null) ||
            input.requirements.some(([r, v]) => !semver.satisfies(v, r))) {
          throw new Error('Node project and lock contain invalid or inconsistent semantic versions.');
        }
        """;

    internal static bool IsNodeVariable(string key) => key.StartsWith("NODE_", StringComparison.OrdinalIgnoreCase) ||
        key.StartsWith("NPM_CONFIG_", StringComparison.OrdinalIgnoreCase) || key.StartsWith("PNPM_", StringComparison.OrdinalIgnoreCase) ||
        key.StartsWith("COREPACK_", StringComparison.OrdinalIgnoreCase);

    internal static Dictionary<string, string?> CleanEnvironment()
    {
        Dictionary<string, string?> result = new(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry item in Environment.GetEnvironmentVariables())
            if (IsNodeVariable((string)item.Key)) result[(string)item.Key] = null;
        return result;
    }

    private static Dictionary<string, string?> InstallEnvironment(string generation, string node)
    {
        var result = CleanEnvironment();
        result["PATH"] = Path.GetDirectoryName(node);
        result["PNPM_HOME"] = Path.Combine(generation, "config", "pnpm-home");
        result["NPM_CONFIG_USERCONFIG"] = Path.Combine(generation, "config", "user.npmrc");
        result["NPM_CONFIG_GLOBALCONFIG"] = Path.Combine(generation, "config", "global.npmrc");
        // pnpm also reads <getConfigDir()>/rc; userconfig/globalconfig alone do not isolate it.
        result["XDG_CONFIG_HOME"] = Path.Combine(generation, "config", "xdg");
        result["XDG_CACHE_HOME"] = Path.Combine(generation, "config", "xdg-cache");
        result["XDG_DATA_HOME"] = Path.Combine(generation, "config", "xdg-data");
        result["XDG_STATE_HOME"] = Path.Combine(generation, "config", "xdg-state");
        result["TEMP"] = result["TMP"] = Path.Combine(generation, "config", "temp");
        result["CI"] = "true"; result["NO_UPDATE_NOTIFIER"] = "1";
        return result;
    }

    private async Task DownloadAsync(string url, string destination, string sha256, CancellationToken callerToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        try { await DownloadCoreAsync(url, destination, sha256, timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException ex) when (!callerToken.IsCancellationRequested)
        { throw new TimeoutException("Node 工具链下载超时，请重试。", ex); }
    }

    private async Task DownloadCoreAsync(string url, string destination, string sha256, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url); request.Headers.UserAgent.ParseAdd("TractorTool/1.1");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.RequestMessage?.RequestUri?.Scheme != "https" || response.Content.Headers.ContentLength > PackageFiles.MaxDownload)
            throw new InvalidDataException("Node 工具链下载地址或大小无效。");
        await using var input = await response.Content.ReadAsStreamAsync(token);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920]; long length = 0; int count;
        while ((count = await input.ReadAsync(buffer, token)) != 0)
        {
            length += count;
            if (length > PackageFiles.MaxDownload) throw new InvalidDataException("Node 工具链下载超过大小限制。");
            hash.AppendData(buffer, 0, count); await output.WriteAsync(buffer.AsMemory(0, count), token);
        }
        if (!Convert.ToHexString(hash.GetHashAndReset()).Equals(sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Node 工具链附件 SHA256 校验失败。");
    }

    internal static async Task ExtractPnpmAsync(string archive, string destination, CancellationToken token)
    {
        await using var file = File.OpenRead(archive);
        await using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var tar = new TarReader(gzip);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var spellings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        long total = 0; int count = 0;
        while (await tar.GetNextEntryAsync(copyData: false, cancellationToken: token) is { } entry)
        {
            if (++count > 20000 || entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.Directory))
                throw new InvalidDataException("pnpm 工具包包含链接、特殊文件或文件数超限。");
            var name = entry.Name.TrimEnd('/'); var path = PackageFiles.SafePath(destination, name);
            if (!name.StartsWith("package/", StringComparison.Ordinal) && name != "package") throw new InvalidDataException("pnpm 包路径无效。");
            if (!names.Add(name)) throw new InvalidDataException("pnpm 工具包包含重复文件。");
            var parts = name.Split('/');
            for (var i = 1; i <= parts.Length; i++)
            {
                var prefix = string.Join('/', parts.Take(i));
                if (spellings.TryGetValue(prefix, out var previous) && prefix != previous) throw new InvalidDataException("pnpm 工具包路径大小写冲突。");
                spellings[prefix] = prefix;
            }
            total = checked(total + entry.Length);
            if (entry.Length < 0 || total > 512L * 1024 * 1024) throw new InvalidDataException("pnpm 工具包展开大小超限。");
            if (entry.EntryType == TarEntryType.Directory)
            {
                if (entry.Length != 0) throw new InvalidDataException("pnpm 目录条目有无效内容。");
                Directory.CreateDirectory(path); continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
            if (entry.DataStream is not null) await entry.DataStream.CopyToAsync(output, token).ConfigureAwait(false);
            if (output.Length != entry.Length) throw new InvalidDataException("pnpm 工具包文件长度不符。");
        }
    }

    private async Task<ProcessResult> RunCheckedAsync(ProcessRequest request, CancellationToken token)
    {
        var result = await processes.RunAsync(request, token).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            var details = string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput.Trim() : result.StandardError.Trim();
            throw new InvalidOperationException($"Node 环境准备失败（退出码 {result.ExitCode}）：{details}");
        }
        return result;
    }
}
