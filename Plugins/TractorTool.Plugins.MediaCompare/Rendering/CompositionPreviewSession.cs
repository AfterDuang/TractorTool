using Avalonia.Platform;
using Avalonia.Rendering.Composition;
using TractorTool.Plugins.MediaCompare.Media;

namespace TractorTool.Plugins.MediaCompare.Rendering;

/// <summary>Owns one surface; presents and disposal are serialized, including compositor GPU completion.</summary>
internal sealed class CompositionPreviewSession : IAsyncDisposable
{
    private const string HandleType = KnownPlatformGraphicsExternalImageHandleTypes.D3D11TextureGlobalSharedHandle;
    private readonly ICompositionGpuInterop _interop;
    private readonly CompositionDrawingSurface _surface;
    private readonly D3DPreviewRenderer _renderer;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ICompositionImportedGpuImage? _image;
    private bool _disposed;

    private CompositionPreviewSession(ICompositionGpuInterop interop, CompositionDrawingSurface surface, D3DPreviewRenderer renderer)
    { _interop = interop; _surface = surface; _renderer = renderer; }

    public static async Task<CompositionPreviewSession> CreateAsync(Compositor compositor)
    {
        var interop = await compositor.TryGetCompositionGpuInterop();
        if (interop is null || !interop.SupportedImageHandleTypes.Contains(HandleType) ||
            !interop.GetSynchronizationCapabilities(HandleType).HasFlag(CompositionGpuImportedImageSynchronizationCapabilities.KeyedMutex))
            throw new NotSupportedException("当前图形后端不支持 D3D11 共享纹理，媒体预览需要默认硬件渲染后端。");
        var luid = interop.DeviceLuid?.ToArray() ?? throw new NotSupportedException("当前图形后端无法提供显卡标识。");
        var renderer = await Task.Run(() => D3DPreviewRenderer.Create(luid));
        try { return new(interop, compositor.CreateDrawingSurface(), renderer); }
        catch { await Task.Run(renderer.Dispose); throw; }
    }

    public CompositionDrawingSurface Surface => _surface;

    public async Task PresentAsync(FramePair? pair, int width, int height, PreviewView view)
    {
        await _gate.WaitAsync();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_interop.IsLost || _image?.IsLost == true) throw new IOException("GPU 预览设备已重置，请重新打开页面。");
            if (_renderer.Width != width || _renderer.Height != height)
            {
                await ReleaseImageAsync();
                await Task.Run(() => _renderer.Resize(width, height));
                _image = _interop.ImportImage(new PlatformHandle(_renderer.SharedHandle, HandleType),
                    new PlatformGraphicsExternalImageProperties
                    {
                        Width = width, Height = height, Format = PlatformGraphicsExternalImageFormat.B8G8R8A8UNorm,
                        // D3D rendering and pointer coordinates both use a top-left origin.
                        TopLeftOrigin = true
                    });
                await _image.ImportCompleted;
            }
            await Task.Run(() =>
            {
                if (pair is not null) _renderer.Upload(pair);
                _renderer.Render(view);
            });
            // Completion means the compositor has finished reading this image, not merely queued it.
            await _surface.UpdateWithKeyedMutexAsync(_image!, 1, 0);
        }
        finally { _gate.Release(); }
    }

    private async ValueTask ReleaseImageAsync()
    {
        var image = _image; _image = null;
        if (image is not null) await image.DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_disposed) return;
            _disposed = true;
            try { await ReleaseImageAsync(); }
            finally
            {
                try { _surface.Dispose(); }
                finally { await Task.Run(_renderer.Dispose); }
            }
        }
        finally { _gate.Release(); }
    }
}
