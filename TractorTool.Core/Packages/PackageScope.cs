using TractorTool.Contracts.Packages;

namespace TractorTool.Core.Packages;

/// <summary>Plugin ownership; disposed after its processes and plugin cleanup have ended.</summary>
public sealed class PackageScope(ExternalPackageService service, string owner,
    IReadOnlyList<PackageReference> allowed, CancellationToken stoppingToken, bool supportsNativeLibraries = false) : IExternalPackageService, IDisposable
{
    private readonly HashSet<PackageReference> _allowed = allowed.ToHashSet();
    private readonly object _gate = new();
    private readonly HashSet<ScopedLease> _leases = [];
    private bool _disposed;
    public PackageStatus GetStatus(PackageReference package) { Check(package); return service.GetStatus(package); }
    public async Task<IPackageLease> AcquireAsync(PackageReference package, CancellationToken cancellationToken = default)
        => (await TakeLeaseAsync(package, false, cancellationToken).ConfigureAwait(false))!;
    public async Task<IPackageLease> RequestAsync(PackageReference package, CancellationToken cancellationToken = default)
        => await TryRequestAsync(package, cancellationToken).ConfigureAwait(false) ?? throw new OperationCanceledException("已取消依赖安装。");
    public Task<IPackageLease?> TryRequestAsync(PackageReference package, CancellationToken cancellationToken = default)
        => TakeLeaseAsync(package, true, cancellationToken);
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
        lock (_gate) { if (_disposed) return; _disposed = true; leases = _leases.ToArray(); }
        foreach (var lease in leases) lease.Dispose();
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
