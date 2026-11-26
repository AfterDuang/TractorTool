using System.Collections;
using System.Security.Cryptography;
using System.Text.Json;
using TractorTool.Contracts.Processes;

namespace TractorTool.Core.Packages.Python;

internal sealed class PythonEnvironmentInstaller(HttpClient http, IProcessRunner processes, string cache)
{
    internal async Task<PythonEnvironmentStorage.EnvironmentRecord> InstallAsync(string directory,
        PythonEnvironmentDefinition definition, Action<string> phase, CancellationToken token)
    {
        PythonEnvironmentDefinitions.Validate(definition);
        var generation = PythonEnvironmentStorage.NewGeneration(directory, definition.Identity);
        try
        {
            PythonCacheStorage.Ensure(cache);
            var inputs = Path.Combine(generation, "inputs"); Directory.CreateDirectory(inputs);
            var projectPath = Path.Combine(inputs, "pyproject.toml"); var lockPath = Path.Combine(inputs, "uv.lock");
            await File.WriteAllTextAsync(projectPath, definition.ProjectText, PythonEnvironmentDefinitions.Utf8, token);
            await File.WriteAllTextAsync(lockPath, definition.LockText, PythonEnvironmentDefinitions.Utf8, token);
            phase("正在准备 uv…");
            var archive = Path.Combine(generation, "uv.zip");
            await DownloadUvAsync(archive, token).ConfigureAwait(false);
            var uvDirectory = Path.Combine(generation, "uv");
            await PackageFiles.ExtractAsync(archive, uvDirectory, token).ConfigureAwait(false);
            File.Delete(archive);
            var executables = Directory.GetFiles(uvDirectory, "uv.exe", SearchOption.AllDirectories);
            if (executables.Length != 1) throw new InvalidDataException("uv 附件未提供唯一入口。");
            var uv = executables[0]; PackageFiles.CheckPath(uv);
            await File.WriteAllTextAsync(Path.Combine(generation, "empty.netrc"), "", token);
            var environment = InstallEnvironment(generation);
            async Task<ProcessResult> RunUv(params string[] arguments) => await RunCheckedAsync(new(uv,
                [.. arguments, "--no-config", "--no-progress", "--color", "never"])
                { WorkingDirectory = inputs, EnvironmentVariables = environment, Timeout = TimeSpan.FromMinutes(15) }, token).ConfigureAwait(false);
            var version = await RunUv("--version");
            if (!version.StandardOutput.StartsWith("uv " + definition.UvVersion + " ", StringComparison.Ordinal) &&
                version.StandardOutput.Trim() != "uv " + definition.UvVersion) throw new InvalidDataException("uv 版本与固定工具链不符。");
            phase("正在准备 Python…");
            var pythonRequest = $"cpython-{definition.PythonVersion}-windows-x86_64-none";
            await RunUv("python", "install", pythonRequest, "--no-bin", "--no-registry");
            RemoveManagedMinorAlias(Path.Combine(generation, "python"), definition.PythonVersion);
            var found = await RunUv("python", "find", pythonRequest, "--managed-python", "--no-python-downloads", "--offline");
            var python = Path.GetFullPath(found.StandardOutput.Trim());
            if (!Storage.AppPaths.Contains(Path.Combine(generation, "python"), python) || !File.Exists(python))
                throw new InvalidDataException("uv 未返回当前环境托管的 Python。");
            PackageFiles.CheckPath(python);
            phase("正在校验锁定资料…");
            var parsed = await RunCheckedAsync(new(python, ["-I", "-S", "-B", "-X", "utf8", "-c", PythonInputPolicy.ParseScript, projectPath, lockPath])
            { WorkingDirectory = inputs, EnvironmentVariables = environment, Timeout = TimeSpan.FromSeconds(30), OutputLimit = 16 * 1024 * 1024 }, token);
            if (parsed.StandardOutputTruncated) throw new InvalidDataException("Python 锁文件解析结果过大。");
            PythonInputPolicy.Validate(parsed.StandardOutput, definition);
            // Unlike --frozen, --check detects a project/lock mismatch. Offline and no-build forbid filling gaps.
            await RunUv("lock", "--check", "--offline", "--no-build", "--no-sources", "--python", python,
                "--no-python-downloads", "--default-index", definition.Source);
            phase("正在按锁安装 Python 依赖…");
            // Frozen sync conflicts with --no-sources; the input policy already rejects source overrides.
            await RunUv("sync", "--frozen", "--no-install-local", "--no-build", "--no-default-groups", "--no-dev",
                "--link-mode", "copy", "--python", python, "--no-python-downloads", "--default-index", definition.Source,
                "--keyring-provider", "disabled");
            // Verify uv did not rewrite the inputs or create an environment outside this final generation.
            if (await File.ReadAllTextAsync(projectPath, token) != definition.ProjectText ||
                await File.ReadAllTextAsync(lockPath, token) != definition.LockText)
                throw new InvalidDataException("uv 修改了冻结输入，未发布环境。");
            var environmentPython = Path.Combine(generation, "environment", "Scripts", "python.exe");
            PackageFiles.CheckPath(environmentPython);
            ValidateVirtualEnvironment(generation, python);
            var checkedPython = await RunCheckedAsync(new(environmentPython,
                ["-I", "-S", "-B", "-X", "utf8", "-c", "import json,sys; print(json.dumps([sys.version.split()[0],sys.executable,sys.base_prefix]))"])
            { WorkingDirectory = inputs, EnvironmentVariables = environment, Timeout = TimeSpan.FromSeconds(30) }, token);
            using (var json = JsonDocument.Parse(checkedPython.StandardOutput))
            {
                var values = json.RootElement;
                if (values[0].GetString() != definition.PythonVersion ||
                    !string.Equals(Path.GetFullPath(values[1].GetString()!), environmentPython, StringComparison.OrdinalIgnoreCase) ||
                    !Storage.AppPaths.Contains(Path.Combine(generation, "python"), Path.GetFullPath(values[2].GetString()!)))
                    throw new InvalidDataException("Python 解释器版本或隔离目录校验失败。");
            }
            await RunUv("pip", "check", "--python", environmentPython, "--offline", "--no-python-downloads");
            phase("正在校验环境文件…");
            var (hash, bytes) = await PythonEnvironmentStorage.WriteManifestAsync(generation, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return new() { Definition = definition, Generation = Path.GetFileName(generation), InstalledRoot = generation,
                ManifestHash = hash, InstalledBytes = bytes, GenerationHashes = new() { [Path.GetFileName(generation)] = hash } };
        }
        catch
        {
            // The marker is written before any tool runs. Never remove another directory or follow a link.
            try
            {
                PythonEnvironmentStorage.CheckGeneration(generation, definition.Identity);
                RemoveManagedMinorAlias(Path.Combine(generation, "python"), definition.PythonVersion);
                PackageFiles.DeleteOwnedTree(directory, generation, PythonEnvironmentStorage.OwnerFile);
                PythonEnvironmentStorage.DeleteGenerationManifest(generation);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    internal static void RemoveManagedMinorAlias(string installDirectory, string version)
    {
        var parts = Version.Parse(version);
        var alias = Path.Combine(installDirectory, $"cpython-{parts.Major}.{parts.Minor}-windows-x86_64-none");
        var patch = Path.Combine(installDirectory, $"cpython-{version}-windows-x86_64-none");
        PackageFiles.CheckPath(patch);
        var info = new DirectoryInfo(alias);
        if (info.Attributes == (FileAttributes)(-1)) return;
        if ((info.Attributes & FileAttributes.ReparsePoint) == 0 ||
            !string.Equals(info.ResolveLinkTarget(false)?.FullName, patch, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("uv 创建的 Python 次版本别名目标不符合固定版本。");
        // Delete only this known junction, never its target. The normal no-reparse-point rule stays intact.
        Directory.Delete(alias);
    }

    private static void ValidateVirtualEnvironment(string generation, string python)
    {
        var root = Path.Combine(generation, "environment");
        var config = Path.Combine(root, "pyvenv.cfg"); PackageFiles.CheckPath(config);
        if (new FileInfo(config).Length is <= 0 or > 16384) throw new InvalidDataException("Python 虚拟环境配置无效。");
        var values = File.ReadAllLines(config).Where(line => line.Contains('=')).Select(line => line.Split('=', 2))
            .ToDictionary(parts => parts[0].Trim(), parts => parts[1].Trim(), StringComparer.OrdinalIgnoreCase);
        if (!values.TryGetValue("home", out var home) || !string.Equals(Path.GetFullPath(home), Path.GetDirectoryName(python), StringComparison.OrdinalIgnoreCase) ||
            !values.TryGetValue("include-system-site-packages", out var inherited) || inherited != "false")
            throw new InvalidDataException("Python 虚拟环境引用了非托管解释器或系统包。");
        var site = Path.Combine(root, "Lib", "site-packages"); PackageFiles.CheckPath(site);
        foreach (var path in Directory.EnumerateFileSystemEntries(site))
        {
            var name = Path.GetFileName(path);
            if (name.EndsWith(".pth", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("sitecustomize", StringComparison.OrdinalIgnoreCase) || name.StartsWith("sitecustomize.", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("usercustomize", StringComparison.OrdinalIgnoreCase) || name.StartsWith("usercustomize.", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("当前 Python 环境不支持带 .pth 或 sitecustomize/usercustomize 启动钩子的 wheel；请插件作者提供无需启动钩子的依赖。");
        }
    }

    internal static Dictionary<string, string?> CleanEnvironment()
    {
        Dictionary<string, string?> result = new(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            var key = (string)entry.Key;
            if (key.StartsWith("UV_", StringComparison.OrdinalIgnoreCase) || key.StartsWith("PYTHON", StringComparison.OrdinalIgnoreCase) ||
                key.StartsWith("PIP_", StringComparison.OrdinalIgnoreCase) || key.StartsWith("CONDA", StringComparison.OrdinalIgnoreCase) ||
                key.StartsWith("VIRTUAL_ENV", StringComparison.OrdinalIgnoreCase)) result[key] = null;
        }
        result["PYTHONDONTWRITEBYTECODE"] = "1";
        result["PYTHONNOUSERSITE"] = "1";
        return result;
    }

    private Dictionary<string, string?> InstallEnvironment(string generation)
    {
        var result = CleanEnvironment();
        result["UV_CACHE_DIR"] = Path.Combine(cache, "content");
        result["UV_PYTHON_CACHE_DIR"] = Path.Combine(cache, "content", "python-downloads");
        result["UV_PYTHON_INSTALL_DIR"] = Path.Combine(generation, "python");
        result["UV_PYTHON_BIN_DIR"] = Path.Combine(generation, "bin");
        result["UV_PYTHON_NO_REGISTRY"] = "1";
        result["UV_PYTHON_INSTALL_BIN"] = "0";
        result["UV_PYTHON_CPYTHON_BUILD"] = PythonEnvironmentDefinitions.PythonBuild;
        result["UV_PYTHON_INSTALL_MIRROR"] = "https://github.com/astral-sh/python-build-standalone/releases/download";
        result["UV_PROJECT_ENVIRONMENT"] = Path.Combine(generation, "environment");
        result["UV_LINK_MODE"] = "copy";
        result["UV_NO_CONFIG"] = "1";
        result["UV_NO_PROGRESS"] = "1";
        result["UV_KEYRING_PROVIDER"] = "disabled";
        result["UV_CREDENTIALS_DIR"] = Path.Combine(cache, "credentials");
        result["NETRC"] = Path.Combine(generation, "empty.netrc");
        result["UV_NO_HF_TOKEN"] = "1";
        return result;
    }

    private async Task DownloadUvAsync(string destination, CancellationToken token)
    {
        var url = $"https://github.com/astral-sh/uv/releases/download/{PythonEnvironmentDefinitions.UvVersion}/uv-x86_64-pc-windows-msvc.zip";
        using var request = new HttpRequestMessage(HttpMethod.Get, url); request.Headers.UserAgent.ParseAdd("TractorTool/1.1");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.RequestMessage?.RequestUri?.Scheme != "https" || response.Content.Headers.ContentLength > PackageFiles.MaxDownload)
            throw new InvalidDataException("uv 下载地址或大小无效。");
        await using var input = await response.Content.ReadAsStreamAsync(token);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920]; long length = 0; int count;
        while ((count = await input.ReadAsync(buffer, token)) != 0)
        {
            length += count;
            if (length > PackageFiles.MaxDownload) throw new InvalidDataException("uv 下载超过大小限制。");
            hash.AppendData(buffer, 0, count); await output.WriteAsync(buffer.AsMemory(0, count), token);
        }
        if (!Convert.ToHexString(hash.GetHashAndReset()).Equals(PythonEnvironmentDefinitions.UvArchiveSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("uv 附件 SHA256 校验失败。");
    }

    private async Task<ProcessResult> RunCheckedAsync(ProcessRequest request, CancellationToken token)
    {
        var result = await processes.RunAsync(request, token).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"Python 环境准备失败（退出码 {result.ExitCode}）：{result.StandardError.Trim()}");
        return result;
    }
}
