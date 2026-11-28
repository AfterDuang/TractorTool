using TractorTool.Contracts;
using TractorTool.Contracts.Packages;
using TractorTool.Contracts.Processes;
using TractorTool.Core.Packages.Environments;
using TractorTool.Core.Storage;

namespace TractorTool.Core.Packages.Node;

public sealed record NodeInstallPrompt(NodeEnvironmentDefinition Definition, string RequestedBy, bool IsRepair);
public sealed record NodeEnvironmentListItem(NodeEnvironmentDefinition Definition, PackagePhase Phase,
    string Error, string Directory, long? InstalledBytes, IReadOnlyList<string> Users, IReadOnlyList<string> RequiredBy,
    bool CanInstall, bool CanRemove, bool CanCancel)
{
    public string Identity => Definition.Identity;
}
public sealed record NodeMigrationEnvironment(string RelativeDirectory, NodeEnvironmentDefinition Definition);

public sealed partial class NodeEnvironmentService : IAsyncDisposable
{
    private readonly ManagedEnvironmentService<NodeEnvironmentDefinition, NodeEnvironmentPolicy> _service;
    public NodeEnvironmentService(string packageRoot, string rid, HttpClient http, IProcessRunner processes,
        IAppLog log, Func<string, bool> sourceAllowed)
    {
        packageRoot = AppPaths.Absolute(packageRoot);
        var installer = new NodeEnvironmentInstaller(http, processes, Path.Combine(Path.GetDirectoryName(packageRoot)!, "Cache", "Node"));
        _service = new(packageRoot, rid, processes, log, sourceAllowed, installer.InstallAsync);
        _service.ConfirmInstallAsync = (prompt, token) => ConfirmInstallAsync is { } confirm
            ? confirm(new(prompt.Definition, prompt.RequestedBy, prompt.IsRepair), token)
            : throw new InvalidOperationException("宿主尚未连接 Node 环境安装确认。");
    }
    public event Action? Changed { add => _service.Changed += value; remove => _service.Changed -= value; }
    public Func<NodeInstallPrompt, CancellationToken, Task<bool>>? ConfirmInstallAsync { get; set; }
    public string Error => _service.Error;
    public bool CanCleanCache => _service.CanCleanCache;
    public static bool IsOwnedRoot(string directory) => NodeEnvironmentStorage.IsOwnedRoot(directory);
    public void UpdateReferences(IReadOnlyList<NodeEnvironmentDefinition> definitions, bool complete, string error) => _service.UpdateReferences(definitions, complete, error);
    public void InvalidateReferences() => _service.InvalidateReferences();
    public IReadOnlyList<NodeEnvironmentListItem> GetEnvironments() => _service.GetEnvironments().Select(item =>
        new NodeEnvironmentListItem(item.Definition, item.Phase, item.Error, item.Directory, item.InstalledBytes,
            item.Users, item.RequiredBy, item.CanInstall, item.CanRemove, item.CanCancel)).ToArray();
    public async Task<INodeEnvironmentLease?> TryRequestAsync(NodeEnvironmentDefinition definition, string owner,
        CancellationToken token = default, IProcessRunner? runtimeProcesses = null) =>
        await _service.TryRequestAsync(definition, owner, token, runtimeProcesses).ConfigureAwait(false) is { } lease ? new Lease(lease) : null;
    public Task InstallAsync(string identity, CancellationToken token = default) => _service.InstallAsync(identity, token);
    public void Cancel(string identity) => _service.Cancel(identity);
    public Task RemoveAsync(string identity) => _service.RemoveAsync(identity);
    public Task CleanCacheAsync(CancellationToken token = default) => _service.CleanCacheAsync(token);
    public ValueTask DisposeAsync() => _service.DisposeAsync();
    private sealed class Lease(IManagedEnvironmentLease lease) : INodeEnvironmentLease
    {
        public string NodePath => lease.RuntimePath;
        public string Directory => lease.Directory;
        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default) => lease.RunAsync(request, cancellationToken);
        public void Dispose() => lease.Dispose();
    }
}
