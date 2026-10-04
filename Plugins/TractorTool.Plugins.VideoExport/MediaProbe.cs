using System.Globalization;
using System.Text.Json;
namespace TractorTool.Plugins.VideoExport;

public sealed record MediaField(string Name, string Value);
public sealed record MediaInfo(int VideoIndex, int Width, int Height, double SampleAspectRatio, double Rotation,
    double? Duration, double? FrameRate, int AudioTracks, string Codec, string PixelFormat,
    string ColorTransfer, string ColorPrimaries, string FieldOrder, string TimeBase, IReadOnlyList<MediaField> Fields)
{
    public string Report { get; init; } = "";
    public string ColorSpace { get; init; } = "";
    public string ColorRange { get; init; } = "";
    public int DisplayWidth => (int)Math.Round(Math.Abs(Rotation % 180) == 90 ? Height : Width * SampleAspectRatio);
    public int DisplayHeight => (int)Math.Round(Math.Abs(Rotation % 180) == 90 ? Width * SampleAspectRatio : Height);
}
public static class MediaProbe
{
    public static MediaInfo Parse(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("streams", out var streams) || streams.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("媒体信息缺少有效的流列表。");
        var video = streams.EnumerateArray().FirstOrDefault(s => Text(s, "codec_type") == "video" &&
            !(s.TryGetProperty("disposition", out var d) && Number(d, "attached_pic") == 1));
        if (video.ValueKind != JsonValueKind.Object) throw new InvalidDataException("所选文件不包含可读取的视频流。");
        root.TryGetProperty("format", out var format);
        var duration = Number(format, "duration") ?? Number(video, "duration");
        var width = Number(video, "width"); var height = Number(video, "height");
        var audio = streams.EnumerateArray().Where(s => Text(s, "codec_type") == "audio")
            .Select(s => Text(s, "codec_name") ?? "未知编码").ToArray();
        var rate = Ratio(Text(video, "avg_frame_rate"));
        var bitrate = Number(format, "bit_rate"); var bytes = Number(format, "size");
        var rotation = 0d;
        if (video.TryGetProperty("side_data_list", out var sideData) && sideData.ValueKind == JsonValueKind.Array)
            foreach (var data in sideData.EnumerateArray())
                if (Number(data, "rotation") is { } angle) { rotation = angle; break; }
        var sar = Ratio(Text(video, "sample_aspect_ratio")?.Replace(':', '/')) ?? 1;
        if (sar <= 0 || sar > 100) sar = 1;
        var info = new MediaInfo((int)(Number(video, "index") ?? 0), (int)(width ?? 0), (int)(height ?? 0), sar, rotation,
            duration, rate, audio.Length, Text(video, "codec_name") ?? "", Text(video, "pix_fmt") ?? "",
            Text(video, "color_transfer") ?? "", Text(video, "color_primaries") ?? "", Text(video, "field_order") ?? "",
            Text(video, "time_base") ?? "", [])
        {
            ColorSpace = Text(video, "color_space") ?? "",
            ColorRange = Text(video, "color_range") ?? ""
        };
        return info with { Report = MediaReport.Format(root), Fields = [new("容器", Text(format, "format_long_name") ?? Text(format, "format_name") ?? "未知"),
            new("时长", duration is >= 0 ? $"{duration:0.###} 秒" : "未知"),
            new("编码尺寸", width is > 0 && height is > 0 ? $"{width:0} × {height:0}" : "未知"),
            new("显示尺寸", width is > 0 && height is > 0 ? $"{info.DisplayWidth} × {info.DisplayHeight} · 旋转 {rotation:0.##}°" : "未知"),
            new("视频编码", Text(video, "codec_long_name") ?? Text(video, "codec_name") ?? "未知"),
            new("平均帧率", rate is > 0 ? $"{rate:0.###} fps" : "未知"),
            new("音轨", audio.Length == 0 ? "无音轨" : $"{audio.Length} 条 · {string.Join("、", audio)}"),
            new("总码率", bitrate is > 0 ? $"{bitrate / 1000000:0.###} Mbps" : "未知"),
            new("文件大小", bytes is >= 0 ? $"{bytes / 1048576:0.##} MiB" : "未知")] };
    }
    private static string? Text(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static double? Number(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value)) return null;
        return double.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var result) && double.IsFinite(result) ? result : null;
    }
    private static double? Ratio(string? value)
    {
        var parts = value?.Split('/');
        return parts is { Length: 2 } && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var n) &&
            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d > 0 && double.IsFinite(n / d) ? n / d : null;
    }
}
