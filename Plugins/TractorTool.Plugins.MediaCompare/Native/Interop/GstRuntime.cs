using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace TractorTool.Plugins.MediaCompare.Native.Interop;

internal static class GstRuntime
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, nint> Libraries = new(StringComparer.OrdinalIgnoreCase);
    private static string? _root;
    private static bool _resolverInstalled;

    internal static void EnsureInitialized(string runtimeRoot, string cacheRoot)
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("AB 审片当前需要 Windows x64。");
        runtimeRoot = Path.GetFullPath(runtimeRoot);
        lock (Gate)
        {
            if (_root is not null)
            {
                if (!string.Equals(_root, runtimeRoot, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("本进程已加载另一份审片运行库，请显式退出后再启动。");
                return;
            }
            var bin = Path.Combine(runtimeRoot, "bin");
            var plugins = Path.Combine(runtimeRoot, "lib", "gstreamer-1.0");
            if (!File.Exists(Path.Combine(bin, GstNative.Core)) || !Directory.Exists(plugins))
                throw new FileNotFoundException("GStreamer 运行库不完整。请退出重启后，在设置 → 外部依赖 → Packages → DLL 运行库中修复。");
            if (!_resolverInstalled)
            {
                NativeLibrary.SetDllImportResolver(typeof(GstRuntime).Assembly, Resolve);
                _resolverInstalled = true;
            }
            var privateLibraries = Directory.GetFiles(bin, "*.dll");
            foreach (var file in privateLibraries)
            {
                var name = Path.GetFileName(file);
                // MSVC's redistributable is shared by native components by design.
                if (name.ToLowerInvariant() is "msvcp140.dll" or "vcruntime140.dll" or "vcruntime140_1.dll") continue;
                var existing = GetModuleHandle(name);
                if (existing == 0) continue;
                var loadedPath = new StringBuilder(32768);
                if (GetModuleFileName(existing, loadedPath, loadedPath.Capacity) == 0 ||
                    !string.Equals(Path.GetFullPath(file), loadedPath.ToString(), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"另一组件已加载不同位置的 {name}，为避免原生运行库混用，请退出并检查插件兼容性。");
            }
            foreach (var library in new[] { GstNative.GLib, GstNative.GObject, GstNative.Core,
                         GstNative.App, GstNative.Video, GstNative.D3D })
                LoadPrivate(library, bin);
            // GModule opens codec modules from lib/gstreamer-1.0. Preload their packaged
            // bin dependencies with DLL_LOAD_DIR rather than modifying process PATH.
            foreach (var library in privateLibraries.Order(StringComparer.OrdinalIgnoreCase))
                LoadPrivate(Path.GetFileName(library), bin);
            GstNative.gst_version(out var major, out var minor, out var micro, out _);
            if (major != 1 || minor != 28 || micro != 9)
                throw new InvalidOperationException($"审片运行库版本不匹配：需要 1.28.9，当前为 {major}.{minor}.{micro}。");
            if (GstNative.gst_is_initialized() != 0)
                throw new InvalidOperationException("另一个组件已初始化 GStreamer。为避免混用插件运行库，请显式退出后重试。");
            if (Marshal.SizeOf<GstNative.MiniObject>() != 64 ||
                Marshal.OffsetOf<GstNative.Buffer>(nameof(GstNative.Buffer.Pts)).ToInt32() != 72 ||
                Marshal.OffsetOf<GstNative.MessageHeader>(nameof(GstNative.MessageHeader.Type)).ToInt32() != 64 ||
                Marshal.OffsetOf<GstNative.MessageHeader>(nameof(GstNative.MessageHeader.Source)).ToInt32() != 80 ||
                Marshal.OffsetOf<GstNative.D3DMemory>(nameof(GstNative.D3DMemory.Device)).ToInt32() != 112 ||
                Marshal.SizeOf<GstNative.MapInfo>() != 104 || Marshal.OffsetOf<GstNative.MapInfo>(nameof(GstNative.MapInfo.Data)).ToInt32() != 16)
                throw new PlatformNotSupportedException("原生媒体接口的结构布局不匹配。");
            Directory.CreateDirectory(cacheRoot);

            // GStreamer reads these at process initialization. Restore them afterwards;
            // never persist system PATH, registry settings, or another plugin's environment.
            var variables = new Dictionary<string, string>
            {
                ["GST_PLUGIN_SYSTEM_PATH_1_0"] = Path.Combine(runtimeRoot, "no-system-plugins"),
                ["GST_PLUGIN_PATH_1_0"] = plugins,
                ["GST_REGISTRY_1_0"] = Path.Combine(cacheRoot, "gstreamer-1.28.9-x64.bin"),
                ["GST_REGISTRY_FORK"] = "no"
            };
            var previous = variables.Keys.ToDictionary(key => key, Environment.GetEnvironmentVariable);
            try
            {
                foreach (var pair in variables) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
                GstNative.gst_registry_fork_set_enabled(0);
                if (GstNative.gst_init_check(0, 0, out var error) == 0)
                    throw new InvalidOperationException(GstNative.TakeError(error, "无法初始化审片运行库。"));
                // A scanner process and installed system codecs are deliberately not required.
                GstNative.gst_registry_scan_path(GstNative.gst_registry_get(), plugins);
                _root = runtimeRoot;
            }
            finally
            {
                foreach (var pair in previous) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            }
        }
    }

    private static nint Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
        => Libraries.TryGetValue(name, out var handle) ? handle : 0;

    private static void LoadPrivate(string name, string bin)
    {
        if (Libraries.ContainsKey(name)) return;
        var path = Path.Combine(bin, name);
        if (!File.Exists(path)) throw new FileNotFoundException($"审片运行库缺少 {name}。");
        var handle = LoadLibraryEx(path, 0, 0x00000100 | 0x00000800); // DLL_LOAD_DIR | SYSTEM32
        if (handle == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), $"无法装载审片运行库 {name}。");
        // Native DLLs have process lifetime. Keep them pinned while imported GPU frames may exist.
        Libraries.Add(name, handle);
    }

    [DllImport("kernel32.dll", EntryPoint = "LoadLibraryExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint LoadLibraryEx(string path, nint file, uint flags);
    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string name);
    [DllImport("kernel32.dll", EntryPoint = "GetModuleFileNameW", CharSet = CharSet.Unicode)]
    private static extern uint GetModuleFileName(nint module, StringBuilder path, int size);
}
