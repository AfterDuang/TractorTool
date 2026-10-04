using TractorTool.Plugins.VideoExport;

// Pure JSON fixtures only: no user media, tool processes or network access.
var failed = 0;
void Check(string name, Action action)
{
    try { action(); Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex.Message); }
}
void Require(bool value) { if (!value) throw new Exception("Media parsing assertion failed."); }
string Value(IReadOnlyList<MediaField> fields, string name) => fields.Single(f => f.Name == name).Value;
void Reject(string json)
{
    try { MediaProbe.Parse(json); }
    catch (Exception ex) when (ex is InvalidDataException or System.Text.Json.JsonException) { return; }
    throw new Exception("Invalid media was accepted.");
}
Check("Video stream excludes album artwork and keeps all audio tracks", () =>
{
    var fields = MediaProbe.Parse("""
        {"format":{"duration":"12.5","size":"1048576","format_name":"mov,mp4","bit_rate":"1000000"},"streams":[
          {"codec_type":"video","width":100,"height":100,"disposition":{"attached_pic":1}},
          {"codec_type":"video","width":1920,"height":1080,"codec_name":"h264","avg_frame_rate":"30000/1001"},
          {"codec_type":"audio","codec_name":"aac"},{"codec_type":"audio","codec_name":"opus"}]}
        """);
    Require(Value(fields, "编码尺寸") == "1920 × 1080");
    Require(Value(fields, "音轨").Contains("aac、opus"));
    Require(Value(fields, "文件大小") == "1 MiB");
});
Check("Unknown duration, zero frame-rate denominator and no audio stay explicit", () =>
{
    var fields = MediaProbe.Parse("""{"format":{"duration":"N/A","bit_rate":"NaN"},"streams":[{"codec_type":"video","avg_frame_rate":"1/0"}]}""");
    Require(Value(fields, "时长") == "未知" && Value(fields, "平均帧率") == "未知" && Value(fields, "总码率") == "未知");
    Require(Value(fields, "音轨") == "无音轨");
});
Check("Missing container duration uses numeric stream duration", () =>
{
    Require(Value(MediaProbe.Parse("""{"streams":[{"codec_type":"video","duration":5}]}"""), "时长") == "5 秒");
});
Check("Audio-only input and album artwork are not a source video", () =>
{
    Reject("""{"streams":[{"codec_type":"audio"}]}""");
    Reject("""{"streams":[{"codec_type":"video","disposition":{"attached_pic":1}}]}""");
});
Check("Malformed and missing stream data is rejected", () =>
{
    Reject(""); Reject("{"); Reject("{}"); Reject("[]"); Reject("""{"streams":[]}"""); Reject("""{"streams":{}}""");
});
return failed == 0 ? 0 : 1;
