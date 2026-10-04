using System.Text;
using System.Text.Json;

namespace TractorTool.Plugins.VideoExport;

// Keep every returned field, including unknown tags and nested side data.
public static class MediaReport
{
    public static string Format(JsonElement root)
    {
        var text = new StringBuilder();
        foreach (var property in root.EnumerateObject())
        {
            if (property.Name == "streams" && property.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (var stream in property.Value.EnumerateArray())
                {
                    var kind = stream.TryGetProperty("codec_type", out var type) ? type.GetString() : null;
                    var title = kind switch { "video" => "视频轨", "audio" => "音频轨", "subtitle" => "字幕轨", _ => "其他流" };
                    var index = stream.TryGetProperty("index", out var id) ? " #" + id : "";
                    text.AppendLine(title + index).AppendLine(new string('─', 36));
                    Append(text, stream, 0); text.AppendLine();
                }
            }
            else
            {
                text.AppendLine(property.Name switch { "format" => "常规 / 容器", "chapters" => "章节", "programs" => "节目", _ => property.Name })
                    .AppendLine(new string('─', 36));
                Append(text, property.Value, 0); text.AppendLine();
            }
        }
        return text.ToString().TrimEnd();
    }

    private static void Append(StringBuilder text, JsonElement value, int depth)
    {
        var pad = new string(' ', depth * 2);
        if (value.ValueKind == JsonValueKind.Object)
            foreach (var property in value.EnumerateObject())
            {
                text.Append(pad).Append(property.Name).Append(" : ");
                if (property.Value.ValueKind is JsonValueKind.Array or JsonValueKind.Object)
                { text.AppendLine(); Append(text, property.Value, depth + 1); }
                else text.AppendLine(property.Value.ToString());
            }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in value.EnumerateArray())
            { text.Append(pad).Append('[').Append(index++).AppendLine("]"); Append(text, item, depth + 1); }
            if (index == 0) text.Append(pad).AppendLine("无");
        }
        else text.Append(pad).AppendLine(value.ToString());
    }
}
