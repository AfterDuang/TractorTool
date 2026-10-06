using System.Text.Json;
using System.Text.RegularExpressions;
using TractorTool.Contracts;
using TractorTool.Core.Storage;
using TractorTool.Core.Packages;

namespace TractorTool.Core.Plugins;

public sealed record PluginManifest
{
    public int SchemaVersion { get; init; }
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Version { get; init; } = "";
    public string EntryAssembly { get; init; } = "";
    public string EntryType { get; init; } = "";
    public string ApiVersion { get; init; } = "";
    public string UiApiVersion { get; init; } = "";
    public string AvaloniaVersion { get; init; } = "";
    public string[] SupportedPlatforms { get; init; } = [];
    public PluginDependency[] Dependencies { get; init; } = [];
    public string Description { get; init; } = "";
    public string Author { get; init; } = "";
}

public sealed record HostCompatibility(string Platform, string Rid, string UiApiVersion, string AvaloniaVersion)
{
    public string? Check(PluginManifest manifest)
    {
        var supportedUi = manifest.UiApiVersion == UiApiVersion || UiApiVersion switch
        {
            "1.1" => manifest.UiApiVersion == "1.0",
            "1.2" => manifest.UiApiVersion is "1.0" or "1.1",
            "1.3" => manifest.UiApiVersion is "1.0" or "1.1" or "1.2",
            "1.4" => manifest.UiApiVersion is "1.0" or "1.1" or "1.2" or "1.3",
            "1.5" => manifest.UiApiVersion is "1.0" or "1.1" or "1.2" or "1.3" or "1.4",
            _ => false
        };
        var supportedApi = manifest.ApiVersion == PluginApi.Version || manifest.ApiVersion is "1.0" or "1.1" or "1.2" or "1.3" or "1.4" or "1.5" or "1.6";
        if (!supportedApi || !supportedUi)
            return $"协议不兼容：需要 API {manifest.ApiVersion} / UI {manifest.UiApiVersion}，宿主为 {PluginApi.Version} / {UiApiVersion}";
        if (manifest.AvaloniaVersion != AvaloniaVersion)
            return $"Avalonia 版本不兼容：插件 {manifest.AvaloniaVersion}，宿主 {AvaloniaVersion}";
        if (!manifest.SupportedPlatforms.Contains(Platform, StringComparer.OrdinalIgnoreCase) &&
            !manifest.SupportedPlatforms.Contains(Rid, StringComparer.OrdinalIgnoreCase))
            return $"不支持当前平台 {Rid}";
        if (manifest.Dependencies.Length != 0 && manifest.ApiVersion is "1.0" or "1.1") return "使用外部工具必须声明 API 1.2 或更高。";
        return null;
    }
}

public static class ManifestValidator
{
    public static PluginManifest Read(string directory)
    {
        var path = Path.Combine(directory, "plugin.json");
        CheckNoLinks(directory);
        CheckNoLinks(path);
        if (new FileInfo(path).Length > 128 * 1024) throw new InvalidDataException("插件清单超过 128 KiB。");
        var manifest = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(path), JsonOptions.Value)
            ?? throw new InvalidDataException("清单为空。");
        Validate(manifest, directory);
        return manifest;
    }

    public static void Validate(PluginManifest m, string directory)
    {
        if (m.SchemaVersion != 1) throw new InvalidDataException("不支持的清单 schemaVersion。");
        AppPaths.ValidateId(m.Id);
        if (string.IsNullOrWhiteSpace(m.Name) || string.IsNullOrWhiteSpace(m.EntryType))
            throw new InvalidDataException("清单缺少名称或入口类型。");
        ValidateVersion(m.Version);
        var info = new DirectoryInfo(directory);
        if (!string.Equals(info.Name, m.Version, StringComparison.Ordinal) ||
            !string.Equals(info.Parent?.Name, m.Id, StringComparison.Ordinal))
            throw new InvalidDataException("清单 id/version 与 Plugins/<PluginId>/<Version> 目录不一致。");
        if (string.IsNullOrWhiteSpace(m.ApiVersion) || string.IsNullOrWhiteSpace(m.UiApiVersion) || string.IsNullOrWhiteSpace(m.AvaloniaVersion))
            throw new InvalidDataException("清单缺少 SDK 版本声明。");
        if (m.SupportedPlatforms is null || m.SupportedPlatforms.Length == 0 || m.SupportedPlatforms.Any(string.IsNullOrWhiteSpace) || m.Dependencies is null)
            throw new InvalidDataException("清单平台或依赖列表无效。");
        if (m.Dependencies.Length > 32 || m.Dependencies.Select(d => d?.Package).Distinct().Count() != m.Dependencies.Length)
            throw new InvalidDataException("清单依赖过多或重复。");
        foreach (var dependency in m.Dependencies)
        {
            if (dependency is null) throw new InvalidDataException("清单依赖不能为空。");
            AppPaths.ValidateId(dependency.Id); ValidateVersion(dependency.Version);
            if (dependency.Downloads is null || dependency.Downloads.Length > 16 ||
                dependency.Downloads.Select(d => d?.Rid).Distinct(StringComparer.OrdinalIgnoreCase).Count() != dependency.Downloads.Length)
                throw new InvalidDataException("依赖下载平台无效或重复。");
            foreach (var download in dependency.Downloads)
                _ = (download ?? throw new InvalidDataException("依赖下载资料为空。")).Definition(dependency.Package);
        }
        _ = EntryPath(m, directory);
    }

    public static void ValidateVersion(string value)
    {
        if (!Regex.IsMatch(value ?? "", @"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$") || !Version.TryParse(value, out _))
            throw new InvalidDataException("插件版本须为三段数字版本。");
    }

    public static string EntryPath(PluginManifest m, string directory)
    {
        var entry = m.EntryAssembly;
        if (string.IsNullOrWhiteSpace(entry) || Path.IsPathRooted(entry) || entry.Contains(':') ||
            entry.Split('/', '\\').Any(part => part is ".." or "." or "") ||
            !entry.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("入口程序集必须为包内 DLL 相对路径。");
        return PackagePath(directory, Path.GetFullPath(Path.Combine(directory, entry)));
    }

    // Entry assemblies and private managed/native dependencies obey the same package boundary.
    public static string PackagePath(string directory, string path)
    {
        directory = AppPaths.Absolute(directory);
        path = AppPaths.Absolute(path);
        if (!AppPaths.Contains(directory, path)) throw new InvalidDataException("插件程序集路径越界。");
        // Missing DLL is a load failure; links are rejected before code can execute.
        for (var current = path; ; current = Path.GetDirectoryName(current)!)
        {
            CheckNoLinks(current);
            if (string.Equals(current, directory, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) break;
        }
        return path;
    }

    public static void CheckNoLinks(string path)
    {
        if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException($"插件目录不接受链接或重解析点：{path}");
    }
}
