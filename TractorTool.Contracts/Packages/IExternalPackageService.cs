namespace TractorTool.Contracts.Packages;

public sealed record PackageReference(string Id, string Version);
public enum PackagePhase { Missing, Downloading, Verifying, Installing, Ready, Failed }
public sealed record PackageStatus(PackageReference Package, string Rid, PackagePhase Phase,
    long DownloadedBytes, long? TotalBytes, string Error, IReadOnlyList<string> Users, bool CanCancel,
    string Directory, long? InstalledBytes);

/// <summary>Plugin-scoped dependencies. The host owns sources, installation consent and storage.</summary>
public interface IExternalPackageService
{
    PackageStatus GetStatus(PackageReference package);
    Task<IPackageLease> AcquireAsync(PackageReference package, CancellationToken cancellationToken = default);
    /// <summary>Reuse an installed package, or ask the host to confirm and install it. API 1.3.</summary>
    Task<IPackageLease> RequestAsync(PackageReference package, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("此宿主尚不支持依赖安装申请。");
}

/// <summary>Hold until all processes and output reads using these tools have completed.</summary>
public interface IPackageLease : IDisposable
{
    string Directory { get; }
    IReadOnlyDictionary<string, string> Tools { get; }
}
