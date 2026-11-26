using TractorTool.Contracts;
using TractorTool.Contracts.Processes;
using TractorTool.Core.Packages.Environments;

namespace TractorTool.Core.Packages.Python;

internal sealed class PythonEnvironmentPolicy : EnvironmentPolicy<PythonEnvironmentDefinition>
{
    internal override string Name => "Python";
    internal override string Folder => "uv";
    internal override string OwnerPrefix => "Python";
    internal override void Validate(PythonEnvironmentDefinition definition) => PythonEnvironmentDefinitions.Validate(definition);
    internal override string RuntimePath(string generation, PythonEnvironmentDefinition definition) =>
        Path.Combine(RuntimeDirectory(generation), "Scripts", "python.exe");
    internal override void PrepareGenerationRemoval(string generation, PythonEnvironmentDefinition definition) =>
        PythonEnvironmentInstaller.RemoveManagedMinorAlias(Path.Combine(generation, "python"), definition.PythonVersion);
    internal override ProcessRequest PrepareRequest(ProcessRequest request)
    {
        var environment = request.EnvironmentVariables is null ? new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) :
            new Dictionary<string, string?>(request.EnvironmentVariables, StringComparer.OrdinalIgnoreCase);
        foreach (var key in environment.Keys.Where(IsReservedVariable).ToArray()) environment[key] = null;
        foreach (var value in PythonEnvironmentInstaller.CleanEnvironment()) environment[value.Key] = value.Value;
        return request with { Arguments = ["-I", "-B", "-X", "utf8", .. request.Arguments], EnvironmentVariables = environment };
    }
    internal override async Task CleanCacheAsync(string cache, string generation, IProcessRunner processes, IAppLog log, CancellationToken token)
    {
        string[] candidates;
        try { candidates = Directory.GetFiles(Path.Combine(generation, "uv"), "uv.exe", SearchOption.AllDirectories); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Write(AppLogLevel.Warning, "Packages", "已装 uv 无法用于缓存维护，将仅清理已确认归属的下载缓存。", ex);
            candidates = [];
        }
        if (candidates.Length != 1) { await base.CleanCacheAsync(cache, generation, processes, log, token); return; }
        var variables = PythonEnvironmentInstaller.CleanEnvironment();
        variables["UV_CREDENTIALS_DIR"] = Path.Combine(cache, "credentials");
        variables["NETRC"] = Path.Combine(generation, "empty.netrc");
        var result = await processes.RunAsync(new(candidates[0], ["cache", "clean", "--cache-dir",
            Path.Combine(cache, "content"), "--no-config", "--offline"])
            { WorkingDirectory = generation, EnvironmentVariables = variables, Timeout = TimeSpan.FromMinutes(5) }, token).ConfigureAwait(false);
        if (result.ExitCode != 0) throw new IOException("Python 缓存清理失败：" + result.StandardError);
    }
    private static bool IsReservedVariable(string key) => key.StartsWith("UV_", StringComparison.OrdinalIgnoreCase) ||
        key.StartsWith("PYTHON", StringComparison.OrdinalIgnoreCase) || key.StartsWith("PIP_", StringComparison.OrdinalIgnoreCase) ||
        key.StartsWith("CONDA", StringComparison.OrdinalIgnoreCase) || key.StartsWith("VIRTUAL_ENV", StringComparison.OrdinalIgnoreCase);
}
