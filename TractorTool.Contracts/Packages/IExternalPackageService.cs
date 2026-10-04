namespace TractorTool.Contracts.Packages;

public sealed record PackageReference(string Id, string Version);
public enum PackagePhase { Missing, Downloading, Verifying, Installing, Ready, Failed }
public sealed record PackageStatus(PackageReference Package, string Rid, PackagePhase Phase,
    long DownloadedBytes, long? TotalBytes, string Error, IReadOnlyList<string> Users, bool CanCancel,
    string Directory, long? InstalledBytes);

/// <summary>Read/acquire only. Downloads are an explicit user operation in host settings.</summary>
public interface IExternalPackageService
{
    PackageStatus GetStatus(PackageReference package);
    Task<IPackageLease> AcquireAsync(PackageReference package, CancellationToken cancellationToken = default);
}

/// <summary>Hold until all processes and output reads using these tools have completed.</summary>
public interface IPackageLease : IDisposable
{
    string Directory { get; }
    IReadOnlyDictionary<string, string> Tools { get; }
}
