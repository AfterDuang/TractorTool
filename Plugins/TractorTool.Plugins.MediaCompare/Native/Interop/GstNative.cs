using System.Runtime.InteropServices;

namespace TractorTool.Plugins.MediaCompare.Native.Interop;

// Public GStreamer 1.x C ABI, pinned to the packaged Windows x64 1.28.9 runtime.
// Headers: gstminiobject.h, gstbuffer.h, gstmemory.h, gstd3d11memory.h.
// Only public fields behind upstream access macros are read; reserved fields are layout padding.
internal static partial class GstNative
{
    internal const string Core = "gstreamer-1.0-0.dll";
    internal const string App = "gstapp-1.0-0.dll";
    internal const string Video = "gstvideo-1.0-0.dll";
    internal const string D3D = "gstd3d11-1.0-0.dll";
    internal const string GObject = "gobject-2.0-0.dll";
    internal const string GLib = "glib-2.0-0.dll";
    internal const ulong None = ulong.MaxValue;
    internal const int Time = 3;
    internal const int Null = 1, Paused = 3, Playing = 4;

    [StructLayout(LayoutKind.Sequential)]
    internal struct MiniObject
    {
        internal nuint Type;
        internal int ReferenceCount, LockState;
        internal uint Flags;
        internal nint Copy, Dispose, Free;
        private uint _reserved;
        private nint _reservedPointer;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Buffer
    {
        internal MiniObject MiniObject;
        internal nint Pool;
        internal ulong Pts, Dts, Duration, Offset, OffsetEnd;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Memory
    {
        internal MiniObject MiniObject;
        internal nint Allocator, Parent;
        internal nuint MaximumSize, Alignment, Offset, Size;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct D3DMemory
    {
        internal Memory Memory;
        internal nint Device;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct MapInfo
    {
        internal nint Memory;
        internal int Flags;
        internal nint Data;
        internal nuint Size, MaxSize;
        private nint _user0, _user1, _user2, _user3;
        private nint _reserved0, _reserved1, _reserved2, _reserved3;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Value { internal nuint Type; internal ulong Data0, Data1; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct GError { internal uint Domain; internal int Code; internal nint Message; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Colorimetry { internal int Range, Matrix, Transfer, Primaries; }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int PadProbeCallback(nint pad, nint info, nint data);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void DestroyNotify(nint data);

    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)] internal static extern int gst_is_initialized();
    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)] internal static extern int gst_init_check(nint argc, nint argv, out nint error);
    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)] internal static extern void gst_version(out uint major, out uint minor, out uint micro, out uint nano);
    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)] internal static extern nint gst_registry_get();
    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)] internal static extern int gst_registry_scan_path(nint registry, [MarshalAs(UnmanagedType.LPUTF8Str)] string path);
    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)] internal static extern void gst_registry_fork_set_enabled(int enabled);
    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)] internal static extern nint gst_element_factory_make([MarshalAs(UnmanagedType.LPUTF8Str)] string factory, [MarshalAs(UnmanagedType.LPUTF8Str)] string? name);
    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)] internal static extern nint gst_parse_bin_from_description([MarshalAs(UnmanagedType.LPUTF8Str)] string description, int ghostUnlinkedPads, out nint error);
    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)] internal static extern nint gst_bin_get_by_name(nint bin, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)] internal static extern nint gst_element_get_static_pad(nint element, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    // gulong is unsigned long, 32-bit in the pinned Windows MSVC ABI, including x64.
    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)] internal static extern uint gst_pad_add_probe(nint pad, int mask, PadProbeCallback callback, nint data, DestroyNotify destroy);
    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)] internal static extern void gst_pad_remove_probe(nint pad, uint id);
    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)] internal static extern int gst_pad_probe_info_get_type(nint info);
    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)] internal static extern nint gst_pad_probe_info_get_event(nint info);
    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)] internal static extern int gst_event_has_name(nint ev, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)] internal static extern void gst_event_parse_caps(nint ev, out nint caps);
    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)] internal static extern int gst_caps_is_equal(nint a, nint b);
    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)] internal static extern void gst_util_set_object_arg(nint obj, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);
    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)] internal static extern int gst_element_set_state(nint element, int state);
    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)] internal static extern int gst_element_get_state(nint element, out int state, out int pending, ulong timeout);
    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)] internal static extern int gst_element_query_duration(nint element, int format, out long duration);
    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)] internal static extern int gst_element_seek_simple(nint element, int format, int flags, long position);
    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)] internal static extern void gst_element_set_start_time(nint element, ulong time);
    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)] internal static extern void gst_element_set_base_time(nint element, ulong time);
    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)] internal static extern void gst_pipeline_use_clock(nint pipeline, nint clock);
    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)] internal static extern nint gst_system_clock_obtain();
    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)] internal static extern ulong gst_clock_get_time(nint clock);
    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)] internal static extern nint gst_element_get_bus(nint element);
    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)] internal static extern nint gst_bus_timed_pop_filtered(nint bus, ulong timeout, uint types);
    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)] internal static extern void gst_message_parse_error(nint message, out nint error, out nint debug);
    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)] internal static extern void gst_object_unref(nint obj);
    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)] internal static extern nint gst_object_ref_sink(nint obj);
    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)] internal static extern void gst_mini_object_unref(nint obj);
    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)] internal static extern nint gst_mini_object_ref(nint obj);
    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)] internal static extern nint gst_sample_get_buffer(nint sample);
    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)] internal static extern nint gst_sample_get_caps(nint sample);
    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)] internal static extern nint gst_sample_get_segment(nint sample);
    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)] internal static extern ulong gst_segment_to_stream_time(nint segment, int format, ulong position);
    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)] internal static extern nint gst_caps_get_structure(nint caps, uint index);
    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)] internal static extern int gst_structure_get_int(nint structure, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, out int value);
    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)] internal static extern int gst_structure_get_fraction(nint structure, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, out int numerator, out int denominator);
    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)] internal static extern nint gst_structure_get_string(nint structure, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)] internal static extern uint gst_buffer_n_memory(nint buffer);
    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)] internal static extern nint gst_buffer_peek_memory(nint buffer, uint index);
    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)] internal static extern int gst_memory_map(nint memory, ref MapInfo info, int flags);
    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)] internal static extern void gst_memory_unmap(nint memory, ref MapInfo info);
    [DllImport(App, CallingConvention = CallingConvention.Cdecl)] internal static extern nint gst_app_sink_try_pull_preroll(nint sink, ulong timeout);
    [DllImport(App, CallingConvention = CallingConvention.Cdecl)] internal static extern nint gst_app_sink_try_pull_sample(nint sink, ulong timeout);
    [DllImport(App, CallingConvention = CallingConvention.Cdecl)] internal static extern int gst_app_sink_is_eos(nint sink);
    [DllImport(Video, CallingConvention = CallingConvention.Cdecl)] internal static extern int gst_video_colorimetry_from_string(out Colorimetry color, [MarshalAs(UnmanagedType.LPUTF8Str)] string text);
    [DllImport(D3D, CallingConvention = CallingConvention.Cdecl)] internal static extern int gst_is_d3d11_memory(nint memory);
    [DllImport(D3D, CallingConvention = CallingConvention.Cdecl)] internal static extern int gst_d3d11_memory_get_native_type(nint memory);
    [DllImport(D3D, CallingConvention = CallingConvention.Cdecl)] internal static extern nint gst_d3d11_memory_get_resource_handle(nint memory);
    [DllImport(D3D, CallingConvention = CallingConvention.Cdecl)] internal static extern uint gst_d3d11_memory_get_subresource_index(nint memory);
    [DllImport(D3D, CallingConvention = CallingConvention.Cdecl)] internal static extern void gst_d3d11_device_lock(nint device);
    [DllImport(D3D, CallingConvention = CallingConvention.Cdecl)] internal static extern void gst_d3d11_device_unlock(nint device);
    [DllImport(GObject, CallingConvention = CallingConvention.Cdecl)] internal static extern nint g_value_init(ref Value value, nuint type);
    [DllImport(GObject, CallingConvention = CallingConvention.Cdecl)] internal static extern void g_value_set_object(ref Value value, nint obj);
    [DllImport(GObject, CallingConvention = CallingConvention.Cdecl)] internal static extern void g_object_set_property(nint obj, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, ref Value value);
    [DllImport(GObject, CallingConvention = CallingConvention.Cdecl)] internal static extern void g_value_unset(ref Value value);
    [DllImport(GLib, CallingConvention = CallingConvention.Cdecl)] internal static extern void g_error_free(nint error);
    [DllImport(GLib, CallingConvention = CallingConvention.Cdecl)] internal static extern void g_free(nint memory);

    internal static string TakeError(nint error, string fallback)
    {
        if (error == 0) return fallback;
        try { return Marshal.PtrToStringUTF8(Marshal.PtrToStructure<GError>(error).Message) ?? fallback; }
        finally { g_error_free(error); }
    }
    internal static void SetObject(nint obj, string name, nint value)
    {
        var native = new Value();
        g_value_init(ref native, 80); // G_TYPE_OBJECT: fundamental type 20 << 2.
        try { g_value_set_object(ref native, value); g_object_set_property(obj, name, ref native); }
        finally { g_value_unset(ref native); }
    }
}
