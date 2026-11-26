using System.Diagnostics;
using System.Runtime.InteropServices;
using TractorTool.Plugins.MediaCompare.Native.Interop;

namespace TractorTool.Plugins.MediaCompare.Media;

/// <summary>All pipeline operations are confined to ComparisonSession's worker.</summary>
internal sealed class GstDecoder : IDisposable
{
    private nint _pipeline, _sinkBin, _sink, _input, _bus, _audioSink, _audioPad;
    private readonly MediaSourceInfo _source;
    private readonly object _capsGate = new();
    private nint _sourcePad, _sourceCaps;
    private uint _capsProbeId;
    private string? _sourceFormat, _sourceFormatError;
    private string _previewWarning = "";
    private bool _collectionKnown, _hasAudioTrack;
    private string? _missingAudioDecoder, _audioWarning;
    private Exception? _lastWarning;
    private static readonly GstNative.PadProbeCallback CapsProbe = OnSourceData;
    private static readonly GstNative.DestroyNotify DestroyProbe = data => GCHandle.FromIntPtr(data).Free();
    private bool _disposed;
    internal MediaDescription Description { get; private set; } = null!;
    internal int FrameRateNumerator { get; private set; }
    internal int FrameRateDenominator { get; private set; }
    internal bool IsStill => _source.IsStill;
    internal bool IsEnded => _sink != 0 && GstNative.gst_app_sink_is_eos(_sink) != 0;

    private GstDecoder(MediaSourceInfo source) => _source = source;

    internal static (GstDecoder Decoder, MediaFrame FirstFrame) Open(MediaSource source, nint clock, CancellationToken token)
    {
        var prepared = MediaSourceInfo.Create(source, token);
        var decoder = new GstDecoder(prepared);
        try
        {
            decoder.Create(clock);
            decoder.Pause();
            var first = decoder.ReadPreroll(token);
            try
            {
                decoder.WaitPaused(token);
                var format = decoder.CheckOriginalFormat();
                var duration = prepared.IsStill ? TimeSpan.Zero : prepared.SequenceDuration ?? decoder.QueryDuration();
                if (!prepared.IsStill && duration <= TimeSpan.Zero)
                    throw new NotSupportedException("媒体没有可用时长，当前仅支持可定位的本地文件。");
                var (audioStatus, audioDescription) = decoder.GetAudioStatus();
                var frameRate = decoder.FrameRateNumerator > 0 && decoder.FrameRateDenominator > 0
                    ? (double)decoder.FrameRateNumerator / decoder.FrameRateDenominator : 0;
                decoder.Description = new(prepared.Name, first.Width, first.Height, duration,
                    prepared.SequenceRate > 0 ? prepared.SequenceRate : frameRate, prepared.IsStill, format,
                    audioStatus, audioDescription, decoder._previewWarning, first.PixelAspectRatio);
                return (decoder, first);
            }
            catch { first.Dispose(); throw; }
        }
        catch { decoder.Dispose(); throw; }
    }

    private void Create(nint clock)
    {
        _pipeline = GstNative.gst_element_factory_make("playbin3", null);
        if (_pipeline == 0) throw new InvalidOperationException("审片运行库缺少 playbin3。");
        GstNative.gst_object_ref_sink(_pipeline);
        // native-video prevents playbin from converting away source bit depth/colorimetry
        // before the first pad of our sink, where we inspect it before showing any frame.
        GstNative.gst_util_set_object_arg(_pipeline, "flags", "83"); // video | audio | soft-volume | native-video
        GstNative.gst_util_set_object_arg(_pipeline, "uri", _source.Uri);
        GstNative.gst_util_set_object_arg(_pipeline, "mute", "true");
        _sinkBin = GstNative.gst_parse_bin_from_description(
            // Preserve native GPU frames when compatible; adapt CPU image formats
            // such as PNG's ARGB64/GRAY16_BE before D3D11 upload.
            "videoconvert name=original ! d3d11upload ! d3d11convert ! video/x-raw(memory:D3D11Memory),format=BGRA ! " +
            "appsink name=frames sync=false max-buffers=3 drop=false wait-on-eos=false", 1, out var error);
        if (error != 0 || _sinkBin == 0)
            throw new InvalidOperationException(GstNative.TakeError(error, "无法建立 D3D11 预览管线。"));
        GstNative.gst_object_ref_sink(_sinkBin);
        _sink = GstNative.gst_bin_get_by_name(_sinkBin, "frames");
        _input = GstNative.gst_bin_get_by_name(_sinkBin, "original");
        if (_sink == 0 || _input == 0) throw new InvalidOperationException("无法建立审片图像接收器。");
        InstallSourceProbe();
        GstNative.SetObject(_pipeline, "video-sink", _sinkBin);
        _audioSink = GstNative.gst_element_factory_make("wasapi2sink", null);
        if (_audioSink == 0) throw new InvalidOperationException("审片运行库缺少 Windows 音频输出模块。");
        GstNative.gst_object_ref_sink(_audioSink);
        _audioPad = GstNative.gst_element_get_static_pad(_audioSink, "sink");
        if (_audioPad == 0) throw new InvalidOperationException("无法检查 Windows 音频输出。");
        GstNative.SetObject(_pipeline, "audio-sink", _audioSink);
        GstNative.gst_pipeline_use_clock(_pipeline, clock);
        _bus = GstNative.gst_element_get_bus(_pipeline);
    }

    internal void Pause()
    {
        if (GstNative.gst_element_set_state(_pipeline, GstNative.Paused) == 0)
        {
            CheckError();
            throw new InvalidOperationException("媒体无法暂停。");
        }
    }

    internal MediaFrame Seek(long positionNanoseconds, CancellationToken token)
    {
        BeginSeek(positionNanoseconds, token);
        return CompleteSeek(token);
    }

    internal void BeginSeek(long positionNanoseconds, CancellationToken token)
    {
        if (IsStill) throw new InvalidOperationException("静态图片不需要定位。");
        token.ThrowIfCancellationRequested();
        Pause();
        // Flush before waiting: PLAYING -> PAUSED may still need a buffer from the
        // old segment, while this worker has stopped consuming the bounded appsink.
        if (GstNative.gst_element_seek_simple(_pipeline, GstNative.Time, 1 | 2, positionNanoseconds) == 0)
            throw new NotSupportedException("此媒体不支持精确定位。");
    }

    internal MediaFrame CompleteSeek(CancellationToken token)
    {
        // The flush clears the old queue. Wait for both the new video preroll and
        // the complete pipeline (including audio) before assigning a shared clock.
        var frame = ReadPreroll(token, afterSeek: true);
        try
        {
            WaitPaused(token);
            return frame;
        }
        catch { frame.Dispose(); throw; }
    }

    internal void Play(ulong baseTime)
    {
        if (IsStill) return;
        // Both bounded appsinks can prefetch; ComparisonSession presents their
        // merged timeline on the shared clock. Audio keeps its clock synchronization.
        GstNative.gst_element_set_start_time(_pipeline, GstNative.None);
        GstNative.gst_element_set_base_time(_pipeline, baseTime);
        if (GstNative.gst_element_set_state(_pipeline, GstNative.Playing) == 0)
        {
            CheckError();
            throw new InvalidOperationException("媒体无法开始播放。");
        }
    }

    internal void EnsureAudioPlayable()
    {
        var (status, description) = GetAudioStatus();
        if (status != MediaAudioStatus.Ready)
            throw new NotSupportedException(description, _lastWarning);
    }

    internal void SetMuted(bool muted)
    {
        GstNative.gst_util_set_object_arg(_pipeline, "mute", muted ? "true" : "false");
    }

    internal MediaFrame? ReadSample(ulong timeout)
    {
        CheckError();
        var sample = GstNative.gst_app_sink_try_pull_sample(_sink, timeout);
        return sample == 0 ? null : ReadFrame(sample);
    }

    private MediaFrame ReadPreroll(CancellationToken token, bool afterSeek = false)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(20))
        {
            token.ThrowIfCancellationRequested();
            CheckError();
            var sample = GstNative.gst_app_sink_try_pull_preroll(_sink, 50_000_000);
            if (sample != 0) return ReadFrame(sample);
            if (afterSeek && IsEnded)
                throw new InvalidDataException("该位置没有可显示的视频帧。媒体总时长可能包含仅有音频的尾段。");
            // is_eos is also true before appsink starts; it cannot diagnose an empty
            // source while the pipeline is still completing its initial preroll.
        }
        throw new TimeoutException("读取媒体帧超时，请检查文件或使用便于逐帧解码的素材。");
    }

    private void WaitPaused(CancellationToken token)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(10))
        {
            token.ThrowIfCancellationRequested();
            CheckError();
            var result = GstNative.gst_element_get_state(_pipeline, out var state, out _, 50_000_000);
            if (result == 0) throw new InvalidOperationException("媒体状态切换失败。");
            if (state == GstNative.Paused && result != 2) return;
        }
        throw new TimeoutException("暂停媒体超时。");
    }

    private MediaFrame ReadFrame(nint sample)
    {
        try
        {
            // The synchronous upstream probe rejects changes before their buffers
            // enter the converter. A queued sample must not use later current caps.
            CheckOriginalFormat();
            var buffer = GstNative.gst_sample_get_buffer(sample);
            var caps = GstNative.gst_sample_get_caps(sample);
            if (buffer == 0 || caps == 0) throw new InvalidDataException("解码器返回了空图像。");
            var structure = GstNative.gst_caps_get_structure(caps, 0);
            if (GstNative.gst_structure_get_int(structure, "width", out var width) == 0 ||
                GstNative.gst_structure_get_int(structure, "height", out var height) == 0 || width is < 1 or > 16384 || height is < 1 or > 16384)
                throw new InvalidDataException("图像尺寸无效或超过当前预览限制。");
            if (Marshal.PtrToStringUTF8(GstNative.gst_structure_get_string(structure, "format")) != "BGRA")
                throw new InvalidDataException("解码图像没有按预览格式输出。");
            var pixelAspectRatio = 1d;
            if (GstNative.gst_structure_get_value(structure, "pixel-aspect-ratio") != 0)
            {
                if (GstNative.gst_structure_get_fraction(structure, "pixel-aspect-ratio", out var aspectNumerator, out var aspectDenominator) == 0 ||
                    aspectNumerator <= 0 || aspectDenominator <= 0)
                    throw new InvalidDataException("媒体像素宽高比无效，无法正确呈现画面。");
                pixelAspectRatio = (double)aspectNumerator / aspectDenominator;
            }
            // Negotiated raw caps use 0/1 for VFR. Never substitute max-framerate
            // or a previous sample's rate when this sample has no fixed rate.
            var hasFixedRate = GstNative.gst_structure_get_fraction(structure, "framerate", out var numerator, out var denominator) != 0 &&
                numerator > 0 && denominator > 0;
            if (!hasFixedRate) numerator = denominator = 0;
            FrameRateNumerator = numerator; FrameRateDenominator = denominator;
            var timing = Marshal.PtrToStructure<GstNative.Buffer>(buffer);
            if (timing.Pts == GstNative.None && !IsStill)
                throw new InvalidDataException("媒体帧没有时间戳，无法保证 A/B 对应关系。");
            var pts = IsStill ? 0 : timing.Pts;
            var segment = GstNative.gst_sample_get_segment(sample);
            var streamTime = segment == 0 ? pts : GstNative.gst_segment_to_stream_time(segment, GstNative.Time, pts);
            if (streamTime == GstNative.None) throw new InvalidDataException("媒体帧时间不在当前播放区间内。");
            // Keep duration for preroll and EOS. Continuous presentation uses the
            // actual successor PTS, which can be quantized differently from duration.
            var end = _source.SequenceRate > 0
                ? FrameTiming.SequenceEnd(streamTime, numerator, denominator)
                : FrameTiming.ResolveEnd(streamTime, timing.Duration);
            var prerollEnd = end ?? FrameTiming.ResolveEnd(streamTime, timing.Duration, numerator, denominator);
            if (GstNative.gst_buffer_n_memory(buffer) == 1)
            {
                var memory = GstNative.gst_buffer_peek_memory(buffer, 0);
                if (GstNative.gst_is_d3d11_memory(memory) != 0)
                {
                    if (GstNative.gst_d3d11_memory_get_native_type(memory) != 2)
                        throw new InvalidDataException("GPU 图像不是受支持的二维纹理。");
                    var device = Marshal.PtrToStructure<GstNative.D3DMemory>(memory).Device;
                    var texture = GstNative.gst_d3d11_memory_get_resource_handle(memory);
                    if (device == 0 || texture == 0) throw new InvalidDataException("GPU 解码帧缺少图像资源。");
                    var access = new GpuFrameAccess(memory, device);
                    return new(width, height, checked((long)streamTime), end, texture,
                        GstNative.gst_d3d11_memory_get_subresource_index(memory),
                        () => GstNative.gst_mini_object_unref(sample),
                        access.Acquire, access.Release, prerollEnd, pixelAspectRatio);
                }
            }
            throw new NotSupportedException("解码器未提供 D3D11 图像。当前预览不能使用不受支持的图像内存。");
        }
        catch { GstNative.gst_mini_object_unref(sample); throw; }
    }

    private sealed class GpuFrameAccess(nint memory, nint device)
    {
        private GstNative.MapInfo _map;
        internal void Acquire()
        {
            GstNative.gst_d3d11_device_lock(device);
            try
            {
                // Public GST_MAP_READ | GST_MAP_D3D11. Mapping uploads CPU staging
                // changes and acquires the source resource's keyed mutex when present.
                _map = default;
                if (GstNative.gst_memory_map(memory, ref _map, 0x20001) == 0)
                    throw new IOException("无法映射解码后的 GPU 图像。");
            }
            catch { GstNative.gst_d3d11_device_unlock(device); throw; }
        }
        internal void Release()
        {
            try { GstNative.gst_memory_unmap(memory, ref _map); }
            finally { GstNative.gst_d3d11_device_unlock(device); }
        }
    }

    private void InstallSourceProbe()
    {
        _sourcePad = GstNative.gst_element_get_static_pad(_input, "sink");
        if (_sourcePad == 0) throw new InvalidOperationException("无法核对源图像格式。");
        var lifetime = GCHandle.Alloc(this);
        try
        {
            // BUFFER | BUFFER_LIST | EVENT_DOWNSTREAM. Public accessors avoid
            // depending on GstPadProbeInfo's platform-specific field layout.
            _capsProbeId = GstNative.gst_pad_add_probe(_sourcePad, 0x70, CapsProbe, GCHandle.ToIntPtr(lifetime), DestroyProbe);
            if (_capsProbeId == 0) throw new InvalidOperationException("无法安装源格式检查。");
        }
        catch { lifetime.Free(); throw; }
    }

    private static int OnSourceData(nint pad, nint info, nint data)
    {
        // GstPadProbeReturn DROP=0, OK=1. Never unwind managed exceptions through C.
        GstDecoder? decoder = null;
        try
        {
            decoder = (GstDecoder)GCHandle.FromIntPtr(data).Target!;
            lock (decoder._capsGate)
            {
                var isEvent = (GstNative.gst_pad_probe_info_get_type(info) & 0x40) != 0;
                var ev = isEvent ? GstNative.gst_pad_probe_info_get_event(info) : 0;
                // A format failure blocks CAPS and buffers, not the stream-control
                // events needed for SEGMENT/EOS propagation and orderly shutdown.
                if (isEvent && (ev == 0 || GstNative.gst_event_has_name(ev, "GstEventCaps") == 0)) return 1;
                if (Volatile.Read(ref decoder._disposed) || decoder._sourceFormatError is not null) return 0;
                if (!isEvent) return 1;
                GstNative.gst_event_parse_caps(ev, out var caps);
                if (caps == 0) throw new InvalidDataException("源媒体缺少图像格式信息。");
                if (decoder._sourceCaps != 0)
                {
                    if (GstNative.gst_caps_is_equal(decoder._sourceCaps, caps) == 0)
                        throw new NotSupportedException("当前审片不支持播放中改变源图像格式或色彩标记，请使用格式一致的素材。");
                    return 1;
                }
                var format = decoder.DescribeSourceFormat(caps);
                decoder._sourceCaps = GstNative.gst_mini_object_ref(caps);
                Volatile.Write(ref decoder._sourceFormat, format);
                return 1;
            }
        }
        catch (Exception error)
        {
            if (decoder is not null) Volatile.Write(ref decoder._sourceFormatError, error.Message);
            return 0;
        }
    }

    private string CheckOriginalFormat()
    {
        if (Volatile.Read(ref _sourceFormatError) is { } error) throw new NotSupportedException(error);
        return Volatile.Read(ref _sourceFormat) ?? throw new InvalidDataException("源媒体缺少图像格式信息。");
    }

    private string DescribeSourceFormat(nint caps)
    {
        var structure = GstNative.gst_caps_get_structure(caps, 0);
        // HDR SEI can survive missing or incorrect transfer/primaries tags.
        if (GstNative.gst_structure_get_value(structure, "mastering-display-info") != 0 ||
            GstNative.gst_structure_get_value(structure, "content-light-level") != 0)
            throw new NotSupportedException("素材包含 HDR 显示元数据，当前尚未实现完整 HDR 显示链。");
        var format = Marshal.PtrToStringUTF8(GstNative.gst_structure_get_string(structure, "format")) ?? "";
        string[] supported = ["BGRA", "RGBA", "ARGB", "ABGR", "BGRx", "RGBx", "xRGB", "xBGR", "RGB", "BGR",
            "I420", "YV12", "NV12", "NV21", "YUY2", "UYVY", "YVYU", "Y42B", "Y444", "GBR", "GRAY8", "AYUV", "VUYA"];
        var depth = supported.Contains(format, StringComparer.Ordinal) ? 8 : format switch
        {
            "P010_10LE" or "P010_10BE" or "I420_10LE" or "I420_10BE" or "I422_10LE" or "I422_10BE" or
            "Y444_10LE" or "Y444_10BE" or "GBR_10LE" or "GBR_10BE" or "GBRA_10LE" or "GBRA_10BE" or
            "RGB10A2_LE" or "BGR10A2_LE" or "Y210" or "Y410" => 10,
            "P012_LE" or "P012_BE" or "I420_12LE" or "I420_12BE" or "I422_12LE" or "I422_12BE" or
            "Y444_12LE" or "Y444_12BE" or "GBR_12LE" or "GBR_12BE" or "GBRA_12LE" or "GBRA_12BE" => 12,
            "ARGB64" or "ARGB64_LE" or "ARGB64_BE" or "RGBA64_LE" or "RGBA64_BE" or
            "BGRA64_LE" or "BGRA64_BE" or "ABGR64_LE" or "ABGR64_BE" or "GRAY16_LE" or "GRAY16_BE" or
            "P016_LE" or "P016_BE" or "GBR_16LE" or "GBR_16BE" or "Y444_16LE" or "Y444_16BE" => 16,
            _ => throw new NotSupportedException($"当前预览尚不支持源像素格式 {format}。")
        };
        _previewWarning = depth > 8 ? $"{depth} 位源 → 8 位预览，不用于判断原始色阶精度" : "";
        var preview = depth > 8 ? $"{depth} 位源 · 8 位预览（降精度）" : "8 位预览";
        var color = Marshal.PtrToStringUTF8(GstNative.gst_structure_get_string(structure, "colorimetry"));
        if (color is null || GstNative.gst_video_colorimetry_from_string(out var data, color) == 0)
            return format + " · " + preview + "；色彩标记不足，未做色彩校准";
        if (data.Transfer is 9 or 10 or 11 or 12 or 13 or 14 or 15 || data.Primaries >= 7)
            throw new NotSupportedException("当前尚未实现 HDR、Log 或广色域到显示器的完整色彩链，不能保证此素材的准确对比。");
        if (data.Transfer == 0 || data.Primaries == 0)
            return format + " · " + preview + "；色彩标记不足，未做色彩校准";
        // Metadata can be absent or incorrect; even recognized SDR tags are not a
        // claim that arbitrary camera Log/ICC content has been color managed.
        return format + " · SDR " + preview + "；未做色彩校准";
    }

    private TimeSpan QueryDuration()
        => GstNative.gst_element_query_duration(_pipeline, GstNative.Time, out var duration) != 0 && duration > 0
            ? FromNanoseconds((ulong)duration) : TimeSpan.Zero;

    internal void CheckError()
    {
        if (Volatile.Read(ref _sourceFormatError) is { } formatError) throw new NotSupportedException(formatError);
        // Filtering only ERROR discards the missing-codec/stream messages needed
        // to distinguish absent audio from an audio stream that could not decode.
        nint message;
        while ((message = GstNative.gst_bus_pop(_bus)) != 0)
        {
            try
            {
                var header = Marshal.PtrToStructure<GstNative.MessageHeader>(message);
                switch (header.Type)
                {
                    case 2: // GST_MESSAGE_ERROR
                        throw ReadNativeError(message, warning: false);
                    case 4: // GST_MESSAGE_WARNING
                        _lastWarning = ReadNativeError(message, warning: true);
                        if (header.Source == _audioSink) _audioWarning = _lastWarning.Message;
                        break;
                    case 0x80000004: // GST_MESSAGE_STREAM_COLLECTION
                        ReadStreamCollection(message);
                        break;
                    default:
                        if (GstNative.gst_is_missing_plugin_message(message) != 0)
                            ReadMissingAudioDecoder(message);
                        break;
                }
            }
            finally { GstNative.gst_mini_object_unref(message); }
        }
    }

    private static Exception ReadNativeError(nint message, bool warning)
    {
        nint error, debug;
        if (warning) GstNative.gst_message_parse_warning(message, out error, out debug);
        else GstNative.gst_message_parse_error(message, out error, out debug);
        try
        {
            var reason = GstNative.TakeError(error, "媒体解码失败。");
            var details = debug == 0 ? null : Marshal.PtrToStringUTF8(debug);
            return new InvalidOperationException(warning ? reason : "无法解码此素材：" + reason,
                string.IsNullOrWhiteSpace(details) ? null : new InvalidOperationException(details));
        }
        finally { if (debug != 0) GstNative.g_free(debug); }
    }

    private void ReadStreamCollection(nint message)
    {
        GstNative.gst_message_parse_stream_collection(message, out var collection);
        if (collection == 0) return;
        try
        {
            _collectionKnown = true;
            for (uint i = 0, count = GstNative.gst_stream_collection_get_size(collection); i < count; i++)
            {
                var stream = GstNative.gst_stream_collection_get_stream(collection, i);
                if (stream != 0 && (GstNative.gst_stream_get_stream_type(stream) & 2) != 0)
                    _hasAudioTrack = true;
            }
        }
        finally { GstNative.gst_object_unref(collection); }
    }

    private void ReadMissingAudioDecoder(nint message)
    {
        var structure = GstNative.gst_message_get_structure(message);
        if (structure == 0 || Marshal.PtrToStringUTF8(GstNative.gst_structure_get_string(structure, "type")) != "decoder") return;
        var detail = GstNative.gst_structure_get_value(structure, "detail");
        var caps = detail == 0 ? 0 : GstNative.g_value_get_boxed(detail);
        if (caps == 0 || GstNative.gst_caps_get_size(caps) == 0) return;
        var mediaType = Marshal.PtrToStringUTF8(GstNative.gst_structure_get_name(GstNative.gst_caps_get_structure(caps, 0)));
        if (mediaType?.StartsWith("audio/", StringComparison.Ordinal) != true) return;
        _hasAudioTrack = true;
        var description = GstNative.gst_missing_plugin_message_get_description(message);
        try { _missingAudioDecoder = Marshal.PtrToStringUTF8(description) ?? mediaType; }
        finally { if (description != 0) GstNative.g_free(description); }
    }

    private (MediaAudioStatus Status, string Description) GetAudioStatus()
    {
        CheckError();
        if (IsStill || _source.SequenceRate > 0) return (MediaAudioStatus.NoTrack, "此素材没有音轨。");
        var caps = GstNative.gst_pad_get_current_caps(_audioPad);
        if (caps != 0)
        {
            try
            {
                if (GstNative.gst_caps_get_size(caps) > 0)
                    return (MediaAudioStatus.Ready, "音轨可用于试听。");
            }
            finally { GstNative.gst_mini_object_unref(caps); }
        }
        if (_missingAudioDecoder is { } missing)
            return (MediaAudioStatus.Unavailable, $"素材有音轨，但当前运行库缺少音频解码器：{missing}。画面仍可比较。");
        if (_audioWarning is { } warning)
            return (MediaAudioStatus.Unavailable, "音频输出不可用：" + warning);
        if (_hasAudioTrack)
            return (MediaAudioStatus.Unavailable, "素材有音轨，但未建立可用的音频输出。画面仍可比较，请查看日志详情。");
        return _collectionKnown
            ? (MediaAudioStatus.NoTrack, "此素材没有音轨。")
            : (MediaAudioStatus.Unknown, "尚未确认此素材的音轨，暂时无法试听。");
    }

    private static TimeSpan FromNanoseconds(ulong value)
    {
        if (value / 100 > long.MaxValue) throw new InvalidDataException("媒体时间戳超出范围。");
        return TimeSpan.FromTicks((long)(value / 100));
    }

    public void Dispose()
    {
        if (_disposed) return;
        Volatile.Write(ref _disposed, true);
        if (_pipeline != 0) GstNative.gst_element_set_state(_pipeline, GstNative.Null);
        if (_capsProbeId != 0) { GstNative.gst_pad_remove_probe(_sourcePad, _capsProbeId); _capsProbeId = 0; }
        lock (_capsGate)
        {
            if (_sourceCaps != 0) { GstNative.gst_mini_object_unref(_sourceCaps); _sourceCaps = 0; }
        }
        foreach (var item in new[] { _sourcePad, _audioPad, _audioSink, _sink, _input, _bus, _sinkBin, _pipeline })
            if (item != 0) GstNative.gst_object_unref(item);
        _sourcePad = _audioPad = _audioSink = _pipeline = _sink = _input = _bus = _sinkBin = 0;
    }
}
