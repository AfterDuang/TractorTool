using System.Text.Json;
using System.Text.Json.Serialization;

namespace TractorTool.Contracts.Packages;

public sealed record PackageReference(string Id, string Version);
[JsonConverter(typeof(PackageKindJsonConverter))]
public enum PackageKind { Tool, NativeLibrary }

internal sealed class PackageKindJsonConverter : JsonStringEnumConverter<PackageKind>
{
    public PackageKindJsonConverter() : base(JsonNamingPolicy.CamelCase, allowIntegerValues: false) { }
}

public enum PackagePhase { Missing, Downloading, Verifying, Installing, Ready, Failed }
public sealed record PackageStatus(PackageReference Package, string Rid, PackagePhase Phase,
    long DownloadedBytes, long? TotalBytes, string Error, IReadOnlyList<string> Users, bool CanCancel,
    string Directory, long? InstalledBytes)
{
    public PackageKind Kind { get; init; }
    /// <summary>Plugins retaining native files until process exit; independent of ordinary lease disposal.</summary>
    public IReadOnlyList<string> RetainedBy { get; init; } = Array.Empty<string>();
}

/// <summary>Plugin-scoped dependencies. The host owns sources, installation consent and storage.</summary>
public interface IExternalPackageService
{
    PackageStatus GetStatus(PackageReference package);
    Task<IPackageLease> AcquireAsync(PackageReference package, CancellationToken cancellationToken = default);
    /// <summary>Reuse an installed package, or ask the host to confirm and install it. API 1.3.</summary>
    Task<IPackageLease> RequestAsync(PackageReference package, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("此宿主尚不支持依赖安装申请。");
    /// <summary>Request a package; null means the user declined installation. Token cancellation still throws. API 1.4.</summary>
    Task<IPackageLease?> TryRequestAsync(PackageReference package, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("此宿主尚不支持可拒绝的依赖安装申请。");
    /// <summary>Request the plugin's declared, locked Python environment. Null means installation was declined. API 1.9.</summary>
    Task<IPythonEnvironmentLease?> TryRequestPythonAsync(string environmentId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("此宿主尚不支持 Python 环境。");
    /// <summary>Request the plugin's declared, locked Node environment. Null means installation was declined. API 1.10.</summary>
    Task<INodeEnvironmentLease?> TryRequestNodeAsync(string environmentId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("此宿主尚不支持 Node 环境。");
}

/// <summary>Hold while using package files. Native libraries additionally require process-lifetime retention before loading.</summary>
public interface IPackageLease : IDisposable
{
    string Directory { get; }
    IReadOnlyDictionary<string, string> Tools { get; }
    /// <summary>Verified absolute paths of declared native entrypoints. API 1.8.</summary>
    IReadOnlyDictionary<string, string> Libraries => throw new NotSupportedException("此宿主尚不支持原生运行库依赖。");
    /// <summary>
    /// Call before the first native load attempt, including attempts that may fail partially.
    /// Prevents replacement and removal until the host process exits, even after this lease is disposed. API 1.8.
    /// </summary>
    void RetainUntilProcessExit() => throw new NotSupportedException("此宿主尚不支持原生运行库占用保护。");
}
