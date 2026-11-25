using TractorTool.Plugins.MediaCompare.Media;

internal static class SequenceChecks
{
    internal static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "TractorTool-SequenceChecks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var first = Touch(root, "镜头 50%_0001.png");
            Touch(root, "镜头 50%_0002.jpg");
            Touch(root, "镜头 50%_00002.png");
            Touch(root, "其他_0002.png");
            Require(Read(first, MediaSourceKind.Auto).IsStill,
                "Different extensions, number widths and prefixes must not form an automatic sequence.");

            Touch(root, "镜头 50%_0002.png");
            var detected = Read(first, MediaSourceKind.Auto, 23.976);
            Require(!detected.IsStill && detected.SequenceDuration is { } duration &&
                Math.Abs(duration.TotalSeconds - 2 * 1001d / 24000) < 1e-7 &&
                detected.Uri.Contains("start-index=1&stop-index=2&framerate=24000/1001", StringComparison.Ordinal),
                "Dropping a numbered first image must detect the contiguous sequence at the selected fractional rate.");
            Require(Uri.UnescapeDataString(detected.Uri).Contains("镜头 50%%_%04d.png", StringComparison.Ordinal),
                "Automatic sequence URI must preserve Unicode and escape literal percent signs for the native template.");
            Require(Read(first, MediaSourceKind.File).IsStill,
                "The ordinary Open action must remain an explicit way to open one image despite numbered neighbors.");

            foreach (var start in new[] { 9, 99 })
            {
                var crossing = Touch(root, $"roll{start}_{start}.png");
                Touch(root, $"roll{start}_{start + 1}.png");
                Require(Read(crossing, MediaSourceKind.Auto).Uri.Contains($"start-index={start}&stop-index={start + 1}", StringComparison.Ordinal),
                    "Sequence discovery must accept natural digit growth with the original minimum width.");
            }

            Touch(root, "镜头 50%_0004.png");
            Reject<InvalidDataException>(() => Read(first, MediaSourceKind.Auto),
                "Automatic detection must report a missing frame instead of silently treating a broken sequence as a still.");

            var unnumbered = Touch(root, "封面.png");
            Require(Read(unnumbered, MediaSourceKind.Auto).IsStill,
                "An unnumbered picture stays a single image.");
            var lone = Touch(root, "单张_0100.jpg");
            Require(Read(lone, MediaSourceKind.Auto).IsStill,
                "A lone numbered picture stays a single image.");
            Require(Read(lone, MediaSourceKind.Sequence).SequenceDuration == TimeSpan.FromSeconds(1d / 24),
                "Explicit sequence selection may contain just one frame.");
            Reject<ArgumentOutOfRangeException>(() => Read(lone, MediaSourceKind.Sequence, double.NaN),
                "An actual sequence must reject an invalid frame rate.");

            var video = Touch(root, "video_0001.mp4");
            Touch(root, "video_0002.mp4");
            var openedVideo = Read(video, MediaSourceKind.Auto);
            Require(!openedVideo.IsStill && openedVideo.SequenceDuration is null && openedVideo.Uri.StartsWith("file:", StringComparison.Ordinal),
                "Numbered videos must remain ordinary media files.");
            Reject<NotSupportedException>(() => Read(video, MediaSourceKind.Sequence),
                "Explicit image-sequence selection must reject videos.");

            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            Reject<OperationCanceledException>(() => MediaSourceInfo.Create(new(first, MediaSourceKind.Auto), cancellation.Token),
                "Automatic discovery must honor cancellation before reading the directory.");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static MediaSourceInfo Read(string path, MediaSourceKind kind, double rate = 24)
        => MediaSourceInfo.Create(new(path, kind, rate), CancellationToken.None);

    private static string Touch(string root, string name)
    {
        var path = Path.Combine(root, name);
        File.WriteAllBytes(path, []);
        return path;
    }

    private static void Reject<T>(Action action, string message) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException(message);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
