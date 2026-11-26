using System.ComponentModel;
using System.Runtime.InteropServices;

namespace NativeLibraryProbePlugin;

internal static class SdlVersionProbe
{
    private static readonly object Gate = new();
    private static nint _library;
    private static string? _path;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetVersion();

    [DllImport("kernel32.dll", EntryPoint = "LoadLibraryExW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint LoadLibraryEx(string fileName, nint file, uint flags);

    public static int Read(string path)
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("此测试运行库仅支持 Windows x64。");
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("运行库路径必须为绝对路径。", nameof(path));
        path = Path.GetFullPath(path);
        lock (Gate)
        {
            if (_library == 0)
            {
                // The caller has already pinned the package. Do not free a partially used native module.
                // The assembly-aware NativeLibrary overload goes through the plugin ALC, whose
                // private dependency resolver intentionally forbids paths outside the plugin ZIP.
                const uint loadLibrarySearchDllLoadDir = 0x00000100;
                const uint loadLibrarySearchSystem32 = 0x00000800;
                _library = LoadLibraryEx(path, 0, loadLibrarySearchDllLoadDir | loadLibrarySearchSystem32);
                if (_library == 0) throw new Win32Exception(Marshal.GetLastPInvokeError(), "SDL 运行库加载失败。");
                _path = path;
            }
            else if (!string.Equals(_path, path, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("本次启动已从另一路径加载 SDL，请退出软件后重试。");
            // SDL documents this function as safe before SDL_Init; no multimedia subsystem is started.
            return Marshal.GetDelegateForFunctionPointer<GetVersion>(NativeLibrary.GetExport(_library, "SDL_GetVersion"))();
        }
    }
}
