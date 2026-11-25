namespace TractorTool.Plugins.MediaCompare.Media;

public enum MediaSide { A, B }
public enum AudioSource { Muted, A, B }
public enum MediaAudioStatus { Unknown, NoTrack, Ready, Unavailable }

public enum MediaSourceKind { File, Sequence, Auto }

public sealed record MediaSource(string Path, MediaSourceKind Kind = MediaSourceKind.File, double SequenceFrameRate = 24);

public sealed record MediaDescription(string Name, int Width, int Height, TimeSpan Duration,
    double FrameRate, bool IsStill, string Format, MediaAudioStatus AudioStatus,
    string AudioDescription, string PreviewWarning = "", double PixelAspectRatio = 1);

/// <summary>One owned decoded sample. Texture pointers remain valid until disposal.</summary>
public sealed class MediaFrame : IDisposable
{
    private static long _nextSampleId;
    private Resource? _resource;
    internal MediaFrame(int width, int height, long timestampNanoseconds, long? endNanoseconds,
        nint texture, uint subresource, Action? release = null,
        Action? lockDevice = null, Action? unlockDevice = null, long? prerollEndNanoseconds = null,
        double pixelAspectRatio = 1)
    {
        if (!double.IsFinite(pixelAspectRatio) || pixelAspectRatio <= 0)
            throw new ArgumentOutOfRangeException(nameof(pixelAspectRatio));
        Width = width; Height = height; TimestampNanoseconds = timestampNanoseconds; EndNanoseconds = endNanoseconds;
        Texture = texture; Subresource = subresource; _resource = new Resource(release);
        PixelAspectRatio = pixelAspectRatio;
        SampleId = Interlocked.Increment(ref _nextSampleId);
        LockDevice = lockDevice; UnlockDevice = unlockDevice;
        PrerollEndNanoseconds = prerollEndNanoseconds ?? endNanoseconds;
    }
    public int Width { get; }
    public int Height { get; }
    public double PixelAspectRatio { get; }
    public double DisplayWidth => Width * PixelAspectRatio;
    public double DisplayHeight => Height;
    /// <summary>Identifies the sample across retained wrappers, independently of recycled native textures or PTS.</summary>
    internal long SampleId { get; }
    internal long TimestampNanoseconds { get; }
    /// <summary>Null means unknown; playback uses the next presentation timestamp as its boundary.</summary>
    internal long? EndNanoseconds { get; }
    /// <summary>Preroll may use fixed-rate caps when no native duration is available.</summary>
    internal long? PrerollEndNanoseconds { get; }
    /// <summary>Decoded BGRA8 ID3D11Texture2D.</summary>
    public nint Texture { get; }
    public uint Subresource { get; }
    public Action? LockDevice { get; }
    public Action? UnlockDevice { get; }
    private MediaFrame(MediaFrame source, Resource resource)
    {
        Width = source.Width; Height = source.Height; TimestampNanoseconds = source.TimestampNanoseconds; EndNanoseconds = source.EndNanoseconds;
        PrerollEndNanoseconds = source.PrerollEndNanoseconds;
        PixelAspectRatio = source.PixelAspectRatio; SampleId = source.SampleId;
        Texture = source.Texture; Subresource = source.Subresource;
        LockDevice = source.LockDevice; UnlockDevice = source.UnlockDevice; _resource = resource;
    }
    internal MediaFrame Retain()
    {
        var resource = Volatile.Read(ref _resource) ?? throw new ObjectDisposedException(nameof(MediaFrame));
        resource.AddReference();
        return new(this, resource);
    }
    public void Dispose() => Interlocked.Exchange(ref _resource, null)?.Release();

    private sealed class Resource(Action? release)
    {
        private int _references = 1;
        public void AddReference()
        {
            while (true)
            {
                var count = Volatile.Read(ref _references);
                if (count == 0) throw new ObjectDisposedException(nameof(MediaFrame));
                if (Interlocked.CompareExchange(ref _references, checked(count + 1), count) == count) return;
            }
        }
        public void Release() { if (Interlocked.Decrement(ref _references) == 0) release?.Invoke(); }
    }
}

/// <summary>Ownership transfers to the receiver, which must release both frames.</summary>
public sealed class FramePair(MediaFrame? a, MediaFrame? b, long positionNanoseconds) : IDisposable
{
    public MediaFrame? A { get; } = a;
    public MediaFrame? B { get; } = b;
    internal long PositionNanoseconds { get; } = positionNanoseconds;
    public TimeSpan Position => TimeSpan.FromTicks(PositionNanoseconds / 100);
    public void Dispose() { A?.Dispose(); if (!ReferenceEquals(A, B)) B?.Dispose(); }
}
