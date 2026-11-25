namespace TractorTool.Plugins.MediaCompare.Media;

public enum MediaSide { A, B }
public enum AudioSource { Muted, A, B }

/// <summary>A sequence is explicitly selected by the user; a still is never guessed to be a sequence.</summary>
public sealed record MediaSource(string Path, bool IsSequence = false, double SequenceFrameRate = 24);

public sealed record MediaDescription(string Name, int Width, int Height, TimeSpan Duration,
    double FrameRate, bool IsStill, string Format);

/// <summary>One owned decoded sample. Texture pointers remain valid until disposal.</summary>
public sealed class MediaFrame : IDisposable
{
    private Resource? _resource;
    public MediaFrame(int width, int height, TimeSpan timestamp, TimeSpan duration,
        nint texture, uint subresource, ReadOnlyMemory<byte> pixels, int stride, Action? release = null,
        Action? lockDevice = null, Action? unlockDevice = null)
    {
        Width = width; Height = height; Timestamp = timestamp; Duration = duration;
        Texture = texture; Subresource = subresource; Pixels = pixels; Stride = stride; _resource = new Resource(release);
        LockDevice = lockDevice; UnlockDevice = unlockDevice;
    }
    public int Width { get; }
    public int Height { get; }
    public TimeSpan Timestamp { get; }
    public TimeSpan Duration { get; }
    /// <summary>BGRA8 ID3D11Texture2D, or zero for a BGRA8 system-memory frame.</summary>
    public nint Texture { get; }
    public uint Subresource { get; }
    public ReadOnlyMemory<byte> Pixels { get; }
    public int Stride { get; }
    public Action? LockDevice { get; }
    public Action? UnlockDevice { get; }
    private MediaFrame(MediaFrame source, Resource resource)
    {
        Width = source.Width; Height = source.Height; Timestamp = source.Timestamp; Duration = source.Duration;
        Texture = source.Texture; Subresource = source.Subresource; Pixels = source.Pixels; Stride = source.Stride;
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
public sealed class FramePair(MediaFrame? a, MediaFrame? b, TimeSpan position) : IDisposable
{
    public MediaFrame? A { get; } = a;
    public MediaFrame? B { get; } = b;
    public TimeSpan Position { get; } = position;
    public void Dispose() { A?.Dispose(); if (!ReferenceEquals(A, B)) B?.Dispose(); }
}
