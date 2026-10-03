using System.Diagnostics;
namespace TractorTool.Platform;
internal static class DesktopActions
{
    public static void OpenDirectory(string path)
    {
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException(path);
        if (OperatingSystem.IsWindows())
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        else
        {
            var start = new ProcessStartInfo(OperatingSystem.IsMacOS() ? "open" : "xdg-open") { UseShellExecute = false };
            start.ArgumentList.Add(path);
            Process.Start(start);
        }
    }
}
