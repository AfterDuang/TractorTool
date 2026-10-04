using System.Collections.ObjectModel;
using TractorTool.Contracts.Packages;

namespace TractorTool.Core.Packages;

// Host-maintained metadata, never supplied by plugin manifests or remote 'latest' feeds.
public sealed record PackageTool(string Path, string VersionPrefix);
public sealed record PackageDefinition(PackageReference Package, string Rid, string Name, string Description,
    Uri DownloadUri, string Sha256, string License, Uri SourceUri, IReadOnlyDictionary<string, PackageTool> Tools);

public static class PackageCatalog
{
    public static IReadOnlyList<PackageDefinition> All { get; } = Array.AsReadOnly(new[]
    {
        new PackageDefinition(new("FFmpeg", "9.0.2"), "win-x64", "FFmpeg / ffprobe",
            "用于视频信息读取与导出。", new("https://www.gyan.dev/ffmpeg/builds/packages/ffmpeg-9.0.2-essentials_build.zip"),
            "60f467265b1e312373dbcd92200c2618a74850f98d3d078e94296bb3fa2047ba", "GPL-3.0",
            new("https://github.com/GyanD/codexffmpeg/releases/tag/9.0.2"),
            new ReadOnlyDictionary<string, PackageTool>(new Dictionary<string, PackageTool>
            {
                ["ffmpeg"] = new("ffmpeg-9.0.2-essentials_build/bin/ffmpeg.exe", "ffmpeg version 9.0.2"),
                ["ffprobe"] = new("ffmpeg-9.0.2-essentials_build/bin/ffprobe.exe", "ffprobe version 9.0.2")
            }))
    });

    public static bool Supports(PackageReference package, string rid) =>
        All.Any(d => d.Package == package && d.Rid == rid);
}
