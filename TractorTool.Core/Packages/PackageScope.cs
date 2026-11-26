using TractorTool.Contracts.Packages;
using TractorTool.Contracts.Processes;
using TractorTool.Core.Packages.Python;
using TractorTool.Core.Packages.Node;

namespace TractorTool.Core.Packages;

/// <summary>Plugin ownership; disposed after its processes and plugin cleanup have ended.</summary>
public sealed class PackageScope(ExternalPackageService service, string owner,
    IReadOnlyList<PackageReference> allowed, CancellationToken stoppingToken, bool supportsNativeLibraries = false,
    IReadOnlyList<PythonEnvironmentDefinition>? pythonEnvironments = null, IProcessRunner? pythonProcesses = null,
    IReadOnlyList<NodeEnvironmentDefinition>? nodeEnvironments = null, IProcessRunner? nodeProcesses = null) : IExternalPackageService, IDisposable
{
    private readonly HashSet<PackageReference> _allowed = allowed.ToHashSet();
    private readonly object _gate = new();
    private readonly HashSet<ScopedLease> _leases = [];
    private readonly IReadOnlyDictionary<string, PythonEnvironmentDefinition> _python =
        (pythonEnvironments ?? []).ToDictionary(p => p.Id, StringComparer.Ordinal);
    private readonly HashSet<ScopedPythonLease> _pythonLeases = [];
    private readonly IReadOnlyDictionary<string, NodeEnvironmentDefinition> _node =
        (nodeEnvironments ?? []).ToDictionary(p => p.Id, StringComparer.Ordinal);
    private readonly HashSet<ScopedNodeLease> _nodeLeases = [];
    private bool _disposed;
    private CancellationToken StoppingToken => stoppingToken;
    public PackageStatus GetStatus(PackageReference package) { Check(package); return service.GetStatus(package); }
    public async Task<IPackageLease> AcquireAsync(PackageReference package, CancellationToken cancellationToken = default)
        => (await TakeLeaseAsync(package, false, cancellationToken).ConfigureAwait(false))!;
    public async Task<IPackageLease> RequestAsync(PackageReference package, CancellationToken cancellationToken = default)
        => await TryRequestAsync(package, cancellationToken).ConfigureAwait(false) ?? throw new OperationCanceledException("已取消依赖安装。");
    public Task<IPackageLease?> TryRequestAsync(PackageReference package, CancellationToken cancellationToken = default)
        => TakeLeaseAsync(package, true, cancellationToken);
    public async Task<IPythonEnvironmentLease?> TryRequestPythonAsync(string environmentId, CancellationToken cancellationToken = default)
    {
        EnsureUsable();
        if (!_python.TryGetValue(environmentId, out var definition))
            throw new InvalidOperationException("插件必须在清单中声明所需 Python 环境并使用 API 1.9。");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stoppingToken);
        var inner = await service.Python.TryRequestAsync(definition, owner, linked.Token, pythonProcesses).ConfigureAwait(false);
        lock (_gate)
        {
            if (_disposed || linked.IsCancellationRequested) { inner?.Dispose(); throw new OperationCanceledException(linked.Token); }
            if (inner is null) return null;
            var lease = new ScopedPythonLease(inner, this);
            _pythonLeases.Add(lease);
            return lease;
        }
    }
    public async Task<INodeEnvironmentLease?> TryRequestNodeAsync(string environmentId, CancellationToken cancellationToken = default)
    {
        EnsureUsable();
        if (!_node.TryGetValue(environmentId, out var definition))
            throw new InvalidOperationException("插件必须在清单中声明所需 Node 环境并使用 API 1.10。");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stoppingToken);
        var inner = await service.Node.TryRequestAsync(definition, owner, linked.Token, nodeProcesses).ConfigureAwait(false);
        lock (_gate)
        {
            if (_disposed || linked.IsCancellationRequested) { inner?.Dispose(); throw new OperationCanceledException(linked.Token); }
            if (inner is null) return null;
            var lease = new ScopedNodeLease(inner, this);
            _nodeLeases.Add(lease);
            return lease;
        }
    }
    private async Task<IPackageLease?> TakeLeaseAsync(PackageReference package, bool request, CancellationToken cancellationToken)
    {
        Check(package);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stoppingToken);
        var inner = request
            ? await service.TryRequestAsync(package, owner, linked.Token, supportsNativeLibraries).ConfigureAwait(false)
            : await service.AcquireAsync(package, owner, linked.Token, supportsNativeLibraries).ConfigureAwait(false);
        lock (_gate)
        {
            if (_disposed || linked.IsCancellationRequested) { inner?.Dispose(); throw new OperationCanceledException(linked.Token); }
            if (inner is null) return null;
            var lease = new ScopedLease(inner, this); _leases.Add(lease); return lease;
        }
    }
    private void Check(PackageReference package)
    {
        EnsureUsable();
        if (!_allowed.Contains(package)) throw new InvalidOperationException("插件必须在清单中声明所需的外部工具及版本。");
    }
    private void EnsureUsable()
    {
        lock (_gate) ObjectDisposedException.ThrowIf(_disposed, this);
        stoppingToken.ThrowIfCancellationRequested();
    }
    public void Dispose()
    {
        ScopedLease[] leases;
        ScopedPythonLease[] pythonLeases;
        ScopedNodeLease[] nodeLeases;
        lock (_gate) { if (_disposed) return; _disposed = true; leases = _leases.ToArray(); pythonLeases = _pythonLeases.ToArray(); nodeLeases = _nodeLeases.ToArray(); }
        foreach (var lease in leases) lease.Dispose();
        foreach (var lease in pythonLeases) lease.Dispose();
        foreach (var lease in nodeLeases) lease.Dispose();
    }
    private sealed class ScopedNodeLease(INodeEnvironmentLease inner, PackageScope scope) : INodeEnvironmentLease
    {
        private int _disposed;
        public string NodePath => inner.NodePath;
        public string Directory => inner.Directory;
        public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
        {
            scope.EnsureUsable();
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, scope.StoppingToken);
            return await inner.RunAsync(request, linked.Token).ConfigureAwait(false);
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (scope._gate) scope._nodeLeases.Remove(this);
            inner.Dispose();
        }
    }
    private sealed class ScopedPythonLease(IPythonEnvironmentLease inner, PackageScope scope) : IPythonEnvironmentLease
    {
        private int _disposed;
        public string PythonPath => inner.PythonPath;
        public string Directory => inner.Directory;
        public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
        {
            scope.EnsureUsable();
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, scope.StoppingToken);
            return await inner.RunAsync(request, linked.Token).ConfigureAwait(false);
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (scope._gate) scope._pythonLeases.Remove(this);
            inner.Dispose();
        }
    }
    private sealed class ScopedLease(IPackageLease inner, PackageScope scope) : IPackageLease
    {
        private int _disposed;
        public string Directory => inner.Directory;
        public IReadOnlyDictionary<string, string> Tools => inner.Tools;
        public IReadOnlyDictionary<string, string> Libraries => inner.Libraries;
        public void RetainUntilProcessExit()
        {
            scope.EnsureUsable();
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            inner.RetainUntilProcessExit();
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (scope._gate) scope._leases.Remove(this);
            inner.Dispose();
        }
    }
}
