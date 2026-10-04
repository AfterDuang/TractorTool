using System.Globalization;
using System.Text.Json;
namespace TractorTool.Plugins.VideoExport;

public sealed record MediaField(string Name, string Value);
public static class MediaProbe
{
    public static IReadOnlyList<MediaField> Parse(string json)
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
        return [new("容器", Text(format, "format_long_name") ?? Text(format, "format_name") ?? "未知"),
            new("时长", duration is >= 0 ? $"{duration:0.###} 秒" : "未知"),
            new("编码尺寸", width is > 0 && height is > 0 ? $"{width:0} × {height:0}" : "未知"),
            new("视频编码", Text(video, "codec_long_name") ?? Text(video, "codec_name") ?? "未知"),
            new("平均帧率", rate is > 0 ? $"{rate:0.###} fps" : "未知"),
            new("音轨", audio.Length == 0 ? "无音轨" : $"{audio.Length} 条 · {string.Join("、", audio)}"),
            new("总码率", bitrate is > 0 ? $"{bitrate / 1000000:0.###} Mbps" : "未知"),
            new("文件大小", bytes is >= 0 ? $"{bytes / 1048576:0.##} MiB" : "未知")];
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
