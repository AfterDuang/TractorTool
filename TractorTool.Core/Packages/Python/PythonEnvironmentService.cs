using TractorTool.Contracts;
using TractorTool.Contracts.Packages;
using TractorTool.Contracts.Processes;
using TractorTool.Core.Packages.Environments;
using TractorTool.Core.Storage;

namespace TractorTool.Core.Packages.Python;

public sealed record PythonInstallPrompt(PythonEnvironmentDefinition Definition, string RequestedBy, bool IsRepair);
public sealed record PythonEnvironmentListItem(PythonEnvironmentDefinition Definition, PackagePhase Phase,
    string Error, string Directory, long? InstalledBytes, IReadOnlyList<string> Users, IReadOnlyList<string> RequiredBy,
    bool CanInstall, bool CanRemove, bool CanCancel)
{
    public string Identity => Definition.Identity;
    public bool IsInstalled => Directory.Length != 0;
}
public sealed record PythonMigrationEnvironment(string RelativeDirectory, PythonEnvironmentDefinition Definition);

public sealed partial class PythonEnvironmentService : IAsyncDisposable
{
    private readonly ManagedEnvironmentService<PythonEnvironmentDefinition, PythonEnvironmentPolicy> _service;
    public PythonEnvironmentService(string packageRoot, string rid, HttpClient http, IProcessRunner processes,
        IAppLog log, Func<string, bool> sourceAllowed)
    {
        packageRoot = AppPaths.Absolute(packageRoot);
        var installer = new PythonEnvironmentInstaller(http, processes, Path.Combine(Path.GetDirectoryName(packageRoot)!, "Cache", "uv"));
        _service = new(packageRoot, rid, processes, log, sourceAllowed, installer.InstallAsync);
        _service.ConfirmInstallAsync = (prompt, token) => ConfirmInstallAsync is { } confirm
            ? confirm(new(prompt.Definition, prompt.RequestedBy, prompt.IsRepair), token)
            : throw new InvalidOperationException("宿主尚未连接 Python 环境安装确认。");
    }
    public event Action? Changed { add => _service.Changed += value; remove => _service.Changed -= value; }
    public Func<PythonInstallPrompt, CancellationToken, Task<bool>>? ConfirmInstallAsync { get; set; }
    public string Error => _service.Error;
    public bool CanCleanCache => _service.CanCleanCache;
    public static bool IsOwnedRoot(string directory) => PythonEnvironmentStorage.IsOwnedRoot(directory);
    public void UpdateReferences(IReadOnlyList<PythonEnvironmentDefinition> definitions, bool complete, string error) => _service.UpdateReferences(definitions, complete, error);
    public void InvalidateReferences() => _service.InvalidateReferences();
    public IReadOnlyList<PythonEnvironmentListItem> GetEnvironments() => _service.GetEnvironments().Select(item =>
        new PythonEnvironmentListItem(item.Definition, item.Phase, item.Error, item.Directory, item.InstalledBytes,
            item.Users, item.RequiredBy, item.CanInstall, item.CanRemove, item.CanCancel)).ToArray();
    public async Task<IPythonEnvironmentLease?> TryRequestAsync(PythonEnvironmentDefinition definition, string owner,
        CancellationToken token = default, IProcessRunner? runtimeProcesses = null) =>
        await _service.TryRequestAsync(definition, owner, token, runtimeProcesses).ConfigureAwait(false) is { } lease ? new Lease(lease) : null;
    public Task InstallAsync(string identity, CancellationToken token = default) => _service.InstallAsync(identity, token);
    public void Cancel(string identity) => _service.Cancel(identity);
    public Task RemoveAsync(string identity) => _service.RemoveAsync(identity);
    public Task CleanCacheAsync(CancellationToken token = default) => _service.CleanCacheAsync(token);
    public ValueTask DisposeAsync() => _service.DisposeAsync();
    private sealed class Lease(IManagedEnvironmentLease lease) : IPythonEnvironmentLease
    {
        public string PythonPath => lease.RuntimePath;
        public string Directory => lease.Directory;
        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default) => lease.RunAsync(request, cancellationToken);
        public void Dispose() => lease.Dispose();
    }
}
