using Avalonia;
using System;

namespace TractorTool;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        LaunchArguments = args;
        var profile = ProfileRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TractorTool");
        if (OperatingSystem.IsWindows() && Path.IsPathFullyQualified(profile))
        {
            using var signal = new Platform.WindowsRestoreSignal(profile);
            if (signal.IsExisting) return;
            RestoreSignal = signal;
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        else BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static string[] LaunchArguments { get; private set; } = [];
    public static string? ProfileRoot
    {
        get
        {
            var index = Array.IndexOf(LaunchArguments, "--profile-root");
            return index < 0 ? null : index + 1 < LaunchArguments.Length ? LaunchArguments[index + 1] : "";
        }
    }
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    internal static Platform.WindowsRestoreSignal? RestoreSignal { get; private set; }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
