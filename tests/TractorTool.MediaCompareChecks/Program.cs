using TractorTool.Plugins.MediaCompare.Media;
using TractorTool.Plugins.MediaCompare.Rendering;

// Pure managed checks: no app, GStreamer initialization, display, or user media.
FrameOwnership();
ConcurrentOwnership();
ViewportGeometry();
SequenceValidation();
Console.WriteLine("MediaCompare managed checks passed.");

static void FrameOwnership()
{
    var releases = 0;
    var original = Frame(() => releases++);
    var retained = original.Retain();
    original.Dispose(); original.Dispose();
    Require(releases == 0, "Pending renderer must retain the sample after decoder releases it.");
    using (var pair = new FramePair(retained, retained, TimeSpan.Zero))
        pair.Dispose();
    Require(releases == 1, "Identical A/B sample and duplicate pair disposal must release once.");
    try { retained.Retain(); throw new Exception("Disposed sample was retained."); }
    catch (ObjectDisposedException) { }

    releases = 0;
    var source = Frame(() => releases++);
    var a = source.Retain(); var b = source.Retain();
    source.Dispose();
    using (var pair = new FramePair(a, b, TimeSpan.Zero)) { }
    Require(releases == 1, "Different wrappers of the same sample must both be released.");
}

static void ConcurrentOwnership()
{
    for (var iteration = 0; iteration < 100; iteration++)
    {
        var releases = 0;
        var source = Frame(() => Interlocked.Increment(ref releases));
        Parallel.For(0, 32, index =>
        {
            if (index == 0) { source.Dispose(); return; }
            try { using var copy = source.Retain(); }
            catch (ObjectDisposedException) { }
        });
        source.Dispose();
        Require(releases == 1, "Concurrent sample handoff must not leak or release twice.");
    }
}

static void ViewportGeometry()
{
    var a = PreviewView.Fit.ImageSize(3840, 2160, 960, 540);
    var b = PreviewView.Fit.ImageSize(1920, 1080, 960, 540);
    Require(a == b && a == (960d, 540d), "Different source sizes must have equal fit framing.");
    var portrait = PreviewView.Fit.ImageSize(1080, 1920, 960, 540);
    Require(Math.Abs(portrait.Width / portrait.Height - 1080d / 1920) < 1e-10 && portrait.Height == 540,
        "Fit must preserve aspect ratio without stretching.");
    var fill = PreviewView.Fill.ImageSize(1920, 1080, 960, 720);
    Require(Math.Abs(fill.Width - 1280) < 1e-10 && fill.Height == 720,
        "Fill must cover the canvas by cropping, without stretching the image.");
    var fillPortrait = PreviewView.Fill.ImageSize(1080, 1920, 960, 540);
    Require(fillPortrait.Width == 960 && fillPortrait.Height > 540,
        "Portrait fill must cover both axes.");
    var pixels = PreviewView.ActualPixels.ImageSize(3840, 2160, 960, 540);
    Require(pixels == (3840d, 2160d), "One-to-one must use source pixels, not fit pixels.");
    var zoom = (PreviewView.Fit with { Zoom = 2 }).ImageSize(1920, 1080, 960, 540);
    Require(zoom == (1920d, 1080d), "Zoom must scale both axes equally.");

    var anchored = PreviewView.Fit.ZoomAt(1920, 1080, 960, 540, 720, 270, 2);
    var anchoredSize = anchored.ImageSize(1920, 1080, 960, 540);
    Require(Math.Abs(anchored.CenterX + 240 / anchoredSize.Width - .75) < 1e-10,
        "Zoom must preserve the source point beneath the pointer.");
    var panned = anchored.Pan(1920, 1080, 960, 540, 192, 108);
    Require(Math.Abs(panned.CenterX - (anchored.CenterX - .1)) < 1e-10 &&
        Math.Abs(panned.CenterY - (anchored.CenterY - .1)) < 1e-10,
        "Pan must use source-relative coordinates shared by both panes.");
    var fillPan = PreviewView.Fill.Pan(1920, 1080, 960, 720, 128, 72);
    Require(Math.Abs(fillPan.CenterX - .4) < 1e-10 && Math.Abs(fillPan.CenterY - .4) < 1e-10,
        "Fill panning must move the image with the pointer in both axes.");
}

static void SequenceValidation()
{
    var root = Path.Combine(Path.GetTempPath(), "TractorTool-MediaCompareChecks-50%-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        // Only enumerate names; these placeholders never go to a decoder.
        var first = Path.Combine(root, "审片 50%_0001.png");
        File.WriteAllBytes(first, []);
        File.WriteAllBytes(Path.Combine(root, "审片 50%_0002.png"), []);
        var sequence = MediaSourceInfo.Create(new(first, true, 23.976), CancellationToken.None);
        Require(Uri.UnescapeDataString(sequence.Uri).Contains("50%%-", StringComparison.Ordinal),
            "Literal percent signs in directories must survive the image-sequence printf template.");
        Require(sequence.SequenceDuration is { } duration && Math.Abs(duration.TotalSeconds - 2 * 1001d / 24000) < 1e-7,
            "Numbered image sequences must use the requested fractional frame rate.");
        File.WriteAllBytes(Path.Combine(root, "审片 50%_0004.png"), []);
        try { MediaSourceInfo.Create(new(first, true), CancellationToken.None); throw new Exception("Gap accepted."); }
        catch (InvalidDataException) { }
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try { MediaSourceInfo.Create(new(first, true), cancelled.Token); throw new Exception("Cancellation ignored."); }
        catch (OperationCanceledException) { }
    }
    finally { Directory.Delete(root, recursive: true); }
}

static MediaFrame Frame(Action release) => new(1, 1, TimeSpan.Zero, TimeSpan.FromSeconds(1d / 24),
    0, 0, new byte[4], 4, release);
static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
