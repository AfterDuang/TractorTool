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
Check("Full report retains secondary tracks, chapters, unknown tags and side data", () =>
{
    var info = MediaProbe.Parse("""{"streams":[{"index":0,"codec_type":"video","side_data_list":[{"custom":"数据"}]},{"index":1,"codec_type":"audio","channels":6},{"index":2,"codec_type":"subtitle","tags":{"language":"zho"}}],"format":{"tags":{"custom_metadata":"作者"}},"chapters":[{"id":1,"start_time":"1.25"}],"programs":[]}""");
    foreach (var value in new[] { "视频轨 #0", "音频轨 #1", "字幕轨 #2", "channels : 6", "language : zho", "custom_metadata : 作者", "start_time : 1.25", "custom : 数据", "节目" })
        Require(info.Report.Contains(value));
});
Check("Old preferences acquire reusable presets without persisting input paths", () =>
{
    var old = System.Text.Json.JsonSerializer.Deserialize<VideoSettings>("""{"SchemaVersion":1,"AutoReadOnSelect":false}""")!;
    Require(!old.AutoReadOnSelect && old.Presets.Length == 1);
    var changed = old with { Presets = [old.Presets[0] with { Name = "4K 200MB 只是名称", Selected = false }] };
    var json = System.Text.Json.JsonSerializer.Serialize(changed);
    var loaded = System.Text.Json.JsonSerializer.Deserialize<VideoSettings>(json)!;
    Require(loaded.Presets[0] == changed.Presets[0] && !json.Contains("SourcePath") && !json.Contains("CoverPath"));
    Require(loaded.Presets[0].Options.Width == 0 && loaded.Presets[0].Options.VideoKbps == 8000);
});
Check("Batch stays serial, skips a cancelled queued item and continues after one failure", () => BatchFixture().GetAwaiter().GetResult());
async Task BatchFixture()
{
    var root = Directory.CreateTempSubdirectory("TractorTool-BatchChecks-").FullName;
    try
    {
        var source = Path.Combine(root, "input.mp4"); await File.WriteAllTextAsync(source, "source");
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0; var active = 0; var maximumActive = 0;
        var runner = new FixtureRunner(async request =>
        {
            if (request.ExecutablePath.EndsWith("ffprobe.exe"))
                return new(0, """{"format":{"duration":"12.5"},"streams":[{"codec_type":"video","index":0,"width":1920,"height":1080,"codec_name":"h264","pix_fmt":"yuv420p"},{"codec_type":"audio"}]}""", "", false, false);
            maximumActive = Math.Max(maximumActive, ++active);
            try
            {
                if (++calls == 1)
                { firstStarted.TrySetResult(); await releaseFirst.Task; return new(1, "", "fixture encoding failure", false, false); }
                await File.WriteAllTextAsync(request.Arguments.Last(), "encoded");
                return new(0, "", "", false, false);
            }
            finally { active--; }
        });
        var store = new FixtureSettings(new() { Presets = Enumerable.Range(1, 3).Select(i => new ExportPresetData(Guid.NewGuid(), "Name " + i, settings)).ToArray() });
        var packages = new FixturePackages();
        var vm = new VideoViewModel(new("Fixture", new FixturePaths(root), store, new FixtureLog(), CancellationToken.None) { Processes = runner, Packages = packages })
            { SourcePath = source, OutputDirectory = root };
        var export = vm.ExportCommand.ExecuteAsync(null);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Require(calls == 1 && vm.IsBusy && !packages.Lease.Disposed);
        vm.Presets[1].CancelCommand.Execute(null);
        vm.DeletePreset(vm.Presets[0]); Require(vm.Presets.Count == 3);
        releaseFirst.TrySetResult(); await export.WaitAsync(TimeSpan.FromSeconds(5));
        Require(maximumActive == 1 && calls == 2 && packages.Lease.Disposed && !vm.IsBusy);
        Require(vm.Presets[0].Status == "导出失败" && vm.Presets[1].Status == "已取消" && vm.Presets[2].Status == "已完成");
        Require(File.Exists(vm.Presets[2].OutputPath) && await File.ReadAllTextAsync(source) == "source");
        await vm.StopAsync();
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

sealed class FixtureSettings(VideoSettings value) : TractorTool.Contracts.IPluginSettingsStore
{
    public T Load<T>(string name) where T : TractorTool.Contracts.IVersionedSettings, new() => (T)(object)value;
    public void Save<T>(string name, T settings) where T : TractorTool.Contracts.IVersionedSettings, new() { }
}
sealed class FixturePaths(string root) : TractorTool.Contracts.IAppPaths
{
    public string AppRoot => root; public string PluginRoot => root; public string DataRoot => root; public string PackageRoot => root;
    public string GetPluginDataDirectory(string pluginId) => root;
}
sealed class FixtureLog : TractorTool.Contracts.IAppLog { public void Write(string source, string message, Exception? error = null) { } }
sealed class FixturePackages : TractorTool.Contracts.Packages.IExternalPackageService
{
    public FixtureLease Lease { get; } = new();
    public TractorTool.Contracts.Packages.PackageStatus GetStatus(TractorTool.Contracts.Packages.PackageReference package) => throw new NotSupportedException();
    public Task<TractorTool.Contracts.Packages.IPackageLease> AcquireAsync(TractorTool.Contracts.Packages.PackageReference package, CancellationToken cancellationToken = default) => Task.FromResult<TractorTool.Contracts.Packages.IPackageLease>(Lease);
    public Task<TractorTool.Contracts.Packages.IPackageLease?> TryRequestAsync(TractorTool.Contracts.Packages.PackageReference package, CancellationToken cancellationToken = default) => Task.FromResult<TractorTool.Contracts.Packages.IPackageLease?>(Lease);
}
sealed class FixtureLease : TractorTool.Contracts.Packages.IPackageLease
{
    public string Directory => "C:/fixture";
    public IReadOnlyDictionary<string, string> Tools { get; } = new Dictionary<string, string> { ["ffmpeg"] = "C:/fixture/ffmpeg.exe", ["ffprobe"] = "C:/fixture/ffprobe.exe" };
    public bool Disposed { get; private set; }
    public void Dispose() => Disposed = true;
}
