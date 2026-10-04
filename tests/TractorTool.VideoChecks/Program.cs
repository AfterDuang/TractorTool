using TractorTool.Plugins.VideoExport;

// Isolated JSON, parameter, stream and temporary-file fixtures; no user media, processes or network.
var failed = 0;
void Check(string name, Action action)
{
    try { action(); Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex.Message); }
}
void Require(bool value) { if (!value) throw new Exception("Media parsing assertion failed."); }
string Value(MediaInfo info, string name) => info.Fields.Single(f => f.Name == name).Value;
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
MediaInfo Sample() => MediaProbe.Parse("""{"format":{"duration":"12.5"},"streams":[{"index":2,"codec_type":"video","width":1920,"height":1080,"codec_name":"h264","pix_fmt":"yuv420p","field_order":"progressive","avg_frame_rate":"25/1","time_base":"1/12800"},{"codec_type":"audio"}]}""");
var settings = new ExportOptions("mp4", "libx264", 0, 0, 8000, 192, false);
Check("Structured media keeps stream identity, rotation, SAR and timebase", () =>
{
    var info = MediaProbe.Parse("""{"streams":[{"index":3,"codec_type":"video","width":720,"height":576,"sample_aspect_ratio":"16:15","time_base":"1/90000","side_data_list":[{"rotation":-90}]}]}""");
    Require(info.VideoIndex == 3 && info.DisplayWidth == 576 && info.DisplayHeight == 768 && info.TimeBase == "1/90000");
});
Check("Export rejects invalid dimensions and unsupported HDR or interlacing", () =>
{
    void Invalid(MediaInfo info, ExportOptions options)
    {
        try { ExportPlan.Validate(info, options, null); }
        catch (Exception e) when (e is InvalidDataException or NotSupportedException) { return; }
        throw new Exception("Unsupported export was accepted.");
    }
    Invalid(Sample(), settings with { Width = 1279, Height = 720 });
    Invalid(Sample(), settings with { Width = 0, Height = 720 });
    Invalid(Sample(), settings with { VideoKbps = 0 });
    Invalid(Sample() with { ColorTransfer = "smpte2084" }, settings);
    Invalid(Sample() with { PixelFormat = "yuv420p9le" }, settings);
    Invalid(Sample() with { PixelFormat = "yuv420p14le" }, settings);
    Invalid(Sample() with { PixelFormat = "gbrpf32le" }, settings);
    Invalid(Sample() with { FieldOrder = "tt" }, settings);
    Invalid(Sample() with { Duration = null }, settings);
});
Check("Export maps the real video stream and all audio without imposing a frame rate", () =>
{
    var args = ExportPlan.Arguments("C:/样本/a video.mp4", Sample(), settings, null, "C:/output.mp4");
    Require(args.Contains("C:/样本/a video.mp4") && args.Contains("0:a?") && args.Contains("passthrough") && !args.Contains("-r"));
    Require(args.Any(a => a.StartsWith("[0:2]")) && args.Contains("-n") && !args.Contains("-y"));
    Require(ExportPlan.EstimateBytes(Sample(), 8000, 192) == 12_800_000);
});
Check("Progress handles split lines, malformed output and oversized lines without reporting success", () =>
{
    List<double> values = [];
    var progress = new ExportProgress(10, values.Add);
    progress.Receive("out_time_"); progress.Receive("us=5000000\r\nprogress=continue\n");
    progress.Receive(new string('x', 10000) + "\nout_time_us=N/A\nprogress=continue\n");
    progress.Receive("out_time_us=15000000\nprogress=end\n");
    progress.Receive("out_time_us=1000000\nprogress=continue\n");
    Require(values.SequenceEqual(new double[] { 50, 99 }));
});
Check("Output validation rejects duration, dimensions and lost audio", () =>
{
    foreach (var output in new[] { Sample() with { Duration = 2 }, Sample() with { Width = 100 }, Sample() with { AudioTracks = 0 } })
    {
        try { ExportPlan.ValidateOutput(Sample(), output, settings); }
        catch (InvalidDataException) { continue; }
        throw new Exception("Invalid output was committed.");
    }
});
Check("Export preserves existing files and removes only its temporary transaction", () =>
{
    ExportFixture(false).GetAwaiter().GetResult();
});
Check("Cancelled export keeps source and previous output intact", () =>
{
    ExportFixture(true).GetAwaiter().GetResult();
});
async Task ExportFixture(bool cancel)
{
    var root = Directory.CreateTempSubdirectory("TractorTool-VideoChecks-").FullName;
    try
    {
        var source = Path.Combine(root, "sample.mp4"); await File.WriteAllTextAsync(source, "source");
        var prior = Path.Combine(root, "sample_导出.mp4"); await File.WriteAllTextAsync(prior, "existing");
        var runner = new FixtureRunner(async request =>
        {
            if (request.ExecutablePath.EndsWith("ffmpeg.exe"))
            {
                await File.WriteAllTextAsync(request.Arguments.Last(), "encoded");
                if (cancel) throw new OperationCanceledException();
                request.StandardOutputReceived?.Invoke("out_time_us=12500000\nprogress=end\n");
                return new(0, "", "", false, false);
            }
            return new(0, """{"format":{"duration":"12.5"},"streams":[{"codec_type":"video","width":1920,"height":1080,"codec_name":"h264"},{"codec_type":"audio"}]}""", "", false, false);
        });
        var exporter = new VideoExporter(runner, ex => throw new Exception("Unexpected cleanup failure", ex));
        string? output = null;
        try
        {
            output = await exporter.ExportAsync(new Dictionary<string, string> { ["ffmpeg"] = "C:/ffmpeg.exe", ["ffprobe"] = "C:/ffprobe.exe" },
                FileStamp.Read(source), Sample(), settings, null, root, _ => { }, _ => { }, CancellationToken.None);
            Require(!cancel);
        }
        catch (OperationCanceledException) when (cancel) { }
        Require(await File.ReadAllTextAsync(source) == "source" && await File.ReadAllTextAsync(prior) == "existing");
        Require(!Directory.EnumerateDirectories(root).Any());
        if (!cancel) Require(output != prior && File.Exists(output));
    }
    finally { Directory.Delete(root, recursive: true); }
}
return failed == 0 ? 0 : 1;

sealed class FixtureRunner(Func<TractorTool.Contracts.Processes.ProcessRequest, Task<TractorTool.Contracts.Processes.ProcessResult>> run)
    : TractorTool.Contracts.Processes.IProcessRunner
{
    public Task<TractorTool.Contracts.Processes.ProcessResult> RunAsync(TractorTool.Contracts.Processes.ProcessRequest request, CancellationToken cancellationToken = default)
        => run(request);
}
