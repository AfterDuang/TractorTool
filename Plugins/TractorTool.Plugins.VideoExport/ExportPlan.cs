namespace TractorTool.Plugins.VideoExport;

public sealed record ExportChoice(string Name, string Value);
public sealed record ExportOptions(string Container, string Encoder, int Width, int Height,
    int VideoKbps, int AudioKbps, bool UseCover);

public static class ExportPlan
{
    public static IReadOnlyList<ExportChoice> Containers { get; } =
        [new("MP4", "mp4"), new("MKV", "matroska"), new("MOV", "mov")];
    public static IReadOnlyList<ExportChoice> Encoders { get; } =
        [new("CPU · H.264", "libx264"), new("NVIDIA · H.264", "h264_nvenc"),
         new("Intel · H.264", "h264_qsv"), new("AMD · H.264", "h264_amf")];

    public static (int Width, int Height) Validate(MediaInfo info, ExportOptions options, string? cover)
    {
        if (!Containers.Any(c => c.Value == options.Container) || !Encoders.Any(c => c.Value == options.Encoder))
            throw new InvalidDataException("请选择支持的输出容器和 H.264 编码方式。");
        if (info.Duration is not > 0 || info.Width <= 0 || info.Height <= 0)
            throw new InvalidDataException("此视频缺少有效时长或尺寸，暂不能导出。");
        if (Math.Abs(info.Rotation % 90) > 0.001)
            throw new NotSupportedException("暂不支持非直角旋转的视频。");
        if (info.ColorTransfer is not ("" or "unknown" or "bt709" or "bt470m" or "bt470bg" or "smpte170m" or "smpte240m" or "gamma22" or "gamma28" or "iec61966-2-1") ||
            info.ColorPrimaries is not ("" or "unknown" or "bt709" or "bt470m" or "bt470bg" or "smpte170m" or "smpte240m") ||
            info.PixelFormat is not ("yuv420p" or "yuv422p" or "yuv444p" or "yuvj420p" or "yuvj422p" or "yuvj444p" or
                "nv12" or "nv21" or "yuyv422" or "uyvy422" or "rgb24" or "bgr24" or "rgba" or "bgra" or "argb" or "abgr" or "gray" or "gbrp"))
            throw new NotSupportedException("当前导出支持 8 位 SDR 视频；HDR、广色域或高位深素材尚未接入色彩转换。");
        if (info.FieldOrder is not ("" or "unknown" or "progressive"))
            throw new NotSupportedException("当前导出暂不支持隔行视频。");
        var width = options.Width == 0 && options.Height == 0 ? info.DisplayWidth : options.Width;
        var height = options.Width == 0 && options.Height == 0 ? info.DisplayHeight : options.Height;
        if (width < 2 || height < 2 || width > 8192 || height > 8192 || width % 2 != 0 || height % 2 != 0)
            throw new InvalidDataException("输出宽高须为 2–8192 内的偶数；源显示尺寸不符合时请指定尺寸。");
        if (options.VideoKbps is < 100 or > 200000 || options.AudioKbps is < 32 or > 512)
            throw new InvalidDataException("视频码率须为 100–200000 kbps，音频码率须为 32–512 kbps。");
        if (options.UseCover && (string.IsNullOrWhiteSpace(cover) || !File.Exists(cover)))
            throw new InvalidDataException("请先选择封面，或关闭替换第一帧。");
        return (width, height);
    }

    public static string Extension(string container) => container == "matroska" ? ".mkv" : "." + container;

    public static IReadOnlyList<string> Arguments(string source, MediaInfo info, ExportOptions options,
        string? cover, string output, bool hardwareCheck = false)
    {
        var (width, height) = Validate(info, options, cover);
        List<string> args = ["-hide_banner", "-loglevel", "error", "-nostdin", "-n", "-copyts", "-start_at_zero", "-i", source];
        if (options.UseCover) args.AddRange(["-i", cover!]);
        var scale = $"scale={width}:{height}:force_original_aspect_ratio=decrease:force_divisible_by=2:reset_sar=1," +
            $"pad={width}:{height}:(ow-iw)/2:(oh-ih)/2,setsar=1";
        var graph = $"[0:{info.VideoIndex}]{scale}[base];";
        if (options.UseCover)
            graph += $"[1:v:0]trim=end_frame=1,setpts=PTS-STARTPTS,scale={width}:{height}:force_original_aspect_ratio=increase:reset_sar=1," +
                $"crop={width}:{height},setsar=1[cover];color=black:s={width}x{height},trim=end_frame=1[background];" +
                "[background][cover]overlay=shortest=1,format=yuv420p[opaque];" +
                "[base][opaque]overlay=enable='eq(n,0)':eof_action=repeat:repeatlast=1,format=yuv420p[out]";
        else graph += "[base]format=yuv420p[out]";
        args.AddRange(["-filter_complex", graph, "-map", "[out]"]);
        if (hardwareCheck) args.AddRange(["-an", "-frames:v", "8"]);
        else args.AddRange(["-map", "0:a?", "-c:a", "aac", "-b:a", $"{options.AudioKbps}k"]);
        args.AddRange(["-c:v", options.Encoder, "-b:v", $"{options.VideoKbps}k", "-pix_fmt", "yuv420p",
            "-fps_mode:v", "passthrough", "-enc_time_base:v", "filter", "-map_metadata", "-1", "-map_chapters", "-1"]);
        if (options.Encoder == "libx264") args.AddRange(["-preset", "medium"]);
        if (options.Container is "mp4" or "mov") args.AddRange(["-movflags", "+faststart"]);
        if (!hardwareCheck) args.AddRange(["-progress", "pipe:1", "-stats_period", "0.5", "-nostats"]);
        args.AddRange(["-f", options.Container, output]);
        return args;
    }

    public static double? EstimateBytes(MediaInfo? info, int videoKbps, int audioKbps) => info?.Duration is > 0
        ? info.Duration * (videoKbps + (double)audioKbps * info.AudioTracks) * 1000 / 8 : null;

    public static void ValidateOutput(MediaInfo source, MediaInfo output, ExportOptions options)
    {
        var width = options.Width == 0 ? source.DisplayWidth : options.Width;
        var height = options.Height == 0 ? source.DisplayHeight : options.Height;
        if (output.Width != width || output.Height != height || output.Codec != "h264" || output.AudioTracks != source.AudioTracks)
            throw new InvalidDataException("输出的画面尺寸、视频编码或音轨数量与导出设置不符，未提交文件。");
        if (output.Duration is not > 0 || source.Duration is not > 0 || Math.Abs(output.Duration.Value - source.Duration.Value) > 1)
            throw new InvalidDataException("输出时长与源视频不符，未提交文件。");
    }
}
