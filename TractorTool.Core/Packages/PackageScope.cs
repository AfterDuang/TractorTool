using TractorTool.Contracts.Packages;

namespace TractorTool.Core.Packages;

/// <summary>Plugin ownership; disposed after its processes and plugin cleanup have ended.</summary>
public sealed class PackageScope(ExternalPackageService service, string owner,
    IReadOnlyList<PackageReference> allowed, CancellationToken stoppingToken) : IExternalPackageService, IDisposable
{
    private readonly HashSet<PackageReference> _allowed = allowed.ToHashSet();
    private readonly object _gate = new();
    private readonly HashSet<ScopedLease> _leases = [];
    private bool _disposed;
    public PackageStatus GetStatus(PackageReference package) { Check(package); return service.GetStatus(package); }
    public async Task<IPackageLease> AcquireAsync(PackageReference package, CancellationToken cancellationToken = default)
    {
        Check(package);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stoppingToken);
        var inner = await service.AcquireAsync(package, owner, linked.Token).ConfigureAwait(false);
        lock (_gate)
        {
            if (_disposed || linked.IsCancellationRequested) { inner.Dispose(); throw new OperationCanceledException(linked.Token); }
            var lease = new ScopedLease(inner, this); _leases.Add(lease); return lease;
        }
    }
    private void Check(PackageReference package)
    {
        lock (_gate) ObjectDisposedException.ThrowIf(_disposed, this);
        stoppingToken.ThrowIfCancellationRequested();
        if (!_allowed.Contains(package)) throw new InvalidOperationException("插件必须在清单中声明所需的外部工具及版本。");
    }
    public void Dispose()
    {
        ScopedLease[] leases;
        lock (_gate) { if (_disposed) return; _disposed = true; leases = _leases.ToArray(); }
        foreach (var lease in leases) lease.Dispose();
    }
    private sealed class ScopedLease(IPackageLease inner, PackageScope scope) : IPackageLease
    {
        private int _disposed;
        public string Directory => inner.Directory;
        public IReadOnlyDictionary<string, string> Tools => inner.Tools;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (scope._gate) scope._leases.Remove(this);
            inner.Dispose();
        }
    }
}
