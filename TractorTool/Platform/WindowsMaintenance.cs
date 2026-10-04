using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace TractorTool.Platform;

[SupportedOSPlatform("windows")]
internal static class WindowsMaintenance
{
    // Match packaging/TractorTool.iss. Multiple profiles may register concurrently;
    // the installer owns the gate for the entire install/uninstall operation.
    internal static Mutex? RegisterApplication()
    {
        using var gate = new Mutex(false, @"Local\TractorTool.Maintenance.Gate");
        bool acquired;
        try { acquired = gate.WaitOne(0); }
        catch (AbandonedMutexException) { acquired = true; }
        if (!acquired)
        {
            MessageBoxW(IntPtr.Zero, "安装或卸载正在进行，请完成后重新打开 TractorTool。", "TractorTool", 0x40);
            return null;
        }
        try
        {
            if (File.Exists(Path.Combine(AppContext.BaseDirectory, ".plugin-seed")))
            {
                MessageBoxW(IntPtr.Zero, "首次安装尚未完成，请重新运行同一安装包完成安装后再打开软件。", "TractorTool", 0x40);
                return null;
            }
            return new Mutex(false, @"Local\TractorTool.Application.Active");
        }
        finally { gate.ReleaseMutex(); }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int MessageBoxW(IntPtr window, string text, string caption, uint type);
}
