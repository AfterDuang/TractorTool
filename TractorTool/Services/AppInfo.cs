using System.Reflection;

namespace TractorTool.Services;

/// <summary>Product metadata of the host, even when hosted by an integration-check executable.</summary>
public static class AppInfo
{
    public static string Version { get; } = typeof(AppInfo).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "未知版本";
}
