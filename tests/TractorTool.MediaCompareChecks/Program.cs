using TractorTool.Plugins.MediaCompare.Media;
using TractorTool.Plugins.MediaCompare.Rendering;

if (args.Length != 0)
    return args[0] is "--media-inputs" or "--seek-play" ? MediaInputDiagnostic.Run(args) : GpuPlaybackDiagnostic.Run(args);

// Pure managed checks: no app, GStreamer initialization, display, or user media.
FrameOwnership();
ConcurrentOwnership();
ViewportGeometry();
TimingChecks.Run();
SequenceChecks.Run();
await PackageRequestChecks.RunAsync();
Console.WriteLine("MediaCompare managed checks passed.");
return 0;

static void FrameOwnership()
{
    var releases = 0;
    var original = Frame(() => releases++);
    var retained = original.Retain();
    Require(original.SampleId == retained.SampleId, "Retained wrappers must keep the same upload identity.");
    using (var another = Frame(() => { }))
        Require(another.SampleId != original.SampleId,
            "New samples must invalidate upload reuse even when texture pointers and timestamps match.");
    original.Dispose(); original.Dispose();
    Require(releases == 0, "Pending renderer must retain the sample after decoder releases it.");
    using (var pair = new FramePair(retained, retained, 0))
        pair.Dispose();
    Require(releases == 1, "Identical A/B sample and duplicate pair disposal must release once.");
    try { retained.Retain(); throw new Exception("Disposed sample was retained."); }
    catch (ObjectDisposedException) { }

    releases = 0;
    var source = Frame(() => releases++);
    var a = source.Retain(); var b = source.Retain();
    source.Dispose();
    using (var pair = new FramePair(a, b, 0)) { }
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

    using var anamorphic = new MediaFrame(720, 576, 0, null, 0, 0, pixelAspectRatio: 16d / 15);
    var corrected = PreviewView.Fit.ImageSize(anamorphic.DisplayWidth, anamorphic.DisplayHeight, 960, 540);
    var square = PreviewView.Fit.ImageSize(768, 576, 960, 540);
    Require(corrected == square && corrected == (720d, 540d),
        "720x576 with 16:15 pixels must match a square-pixel 768x576 reference at 4:3.");
    var correctedZoom = PreviewView.Fit.ZoomAt(anamorphic.DisplayWidth, anamorphic.DisplayHeight, 960, 540, 600, 200, 2);
    var squareZoom = PreviewView.Fit.ZoomAt(768, 576, 960, 540, 600, 200, 2);
    Require(correctedZoom == squareZoom &&
        correctedZoom.Pan(anamorphic.DisplayWidth, anamorphic.DisplayHeight, 960, 540, 48, 27) ==
        squareZoom.Pan(768, 576, 960, 540, 48, 27),
        "Zoom anchoring and linked pan must use display geometry, independently of stored pixel dimensions.");
}

static MediaFrame Frame(Action release) => new(1, 1, 0, 41_666_666,
    0, 0, release);
static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
