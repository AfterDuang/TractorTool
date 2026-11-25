using System.Numerics;
using System.Runtime.InteropServices;
using TractorTool.Plugins.MediaCompare.Media;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using static Vortice.Direct3D11.D3D11;
using static Vortice.DXGI.DXGI;

namespace TractorTool.Plugins.MediaCompare.Rendering;

/// <summary>Single-threaded D3D worker. No decoder-owned texture survives Upload.</summary>
internal sealed class D3DPreviewRenderer : IDisposable
{
    private const int MutexTimeoutMs = 2000;
    private static readonly Color4 Background = new(.035f, .035f, .035f, 1);
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private ID3D11VertexShader? _vertexShader;
    private ID3D11PixelShader? _pixelShader;
    private ID3D11Buffer? _constants;
    private ID3D11SamplerState? _linearSampler;
    private ID3D11SamplerState? _pointSampler;
    private ID3D11RasterizerState? _rasterizer;
    private ID3D11Texture2D? _output;
    private ID3D11RenderTargetView? _target;
    private IDXGIKeyedMutex? _outputMutex;
    private SourceTexture? _a, _b;
    private long _adapterLuid;
    public int Width { get; private set; }
    public int Height { get; private set; }
    public nint SharedHandle { get; private set; }

    public static D3DPreviewRenderer Create(byte[] deviceLuid)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("媒体对比 GPU 预览目前需要 Windows。");
        if (deviceLuid.Length != sizeof(long)) throw new NotSupportedException("图形后端未提供有效的显卡标识，无法安全共享预览纹理。");
        var renderer = new D3DPreviewRenderer { _adapterLuid = BitConverter.ToInt64(deviceLuid) };
        try { renderer.Initialize(); return renderer; }
        catch { renderer.Dispose(); throw; }
    }

    private void Initialize()
    {
        using var factory = CreateDXGIFactory1<IDXGIFactory1>();
        for (uint index = 0; factory.EnumAdapters1(index, out var adapter).Success; index++)
        {
            using (adapter)
            {
                if (adapter.Description1.Luid != _adapterLuid) continue;
                D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport,
                    [FeatureLevel.Level_11_0], out _device, out _, out _context).CheckError();
                break;
            }
        }
        if (_device is null) throw new NotSupportedException("未找到与软件界面相同的显卡，无法共享媒体预览纹理。");
        _vertexShader = _device.CreateVertexShader(Compiler.Compile(Shader, "VS", "ComparePreview", "vs_5_0").Span);
        _pixelShader = _device.CreatePixelShader(Compiler.Compile(Shader, "PS", "ComparePreview", "ps_5_0").Span);
        _constants = _device.CreateBuffer(16, BindFlags.ConstantBuffer);
        _linearSampler = _device.CreateSamplerState(new SamplerDescription(Filter.MinMagMipLinear, TextureAddressMode.Clamp));
        _pointSampler = _device.CreateSamplerState(new SamplerDescription(Filter.MinMagMipPoint, TextureAddressMode.Clamp));
        _rasterizer = _device.CreateRasterizerState(new RasterizerDescription(CullMode.None, FillMode.Solid));
    }

    public void Resize(int width, int height)
    {
        ValidateSize(width, height);
        if (width == Width && height == Height) return;
        ReleaseOutput();
        var description = TextureDescription(width, height, BindFlags.RenderTarget, shared: true);
        _output = _device!.CreateTexture2D(description);
        _target = _device.CreateRenderTargetView(_output);
        _outputMutex = _output.QueryInterface<IDXGIKeyedMutex>();
        using var resource = _output.QueryInterface<IDXGIResource>();
        SharedHandle = resource.SharedHandle;
        Width = width; Height = height;
    }

    public void Upload(FramePair pair)
    {
        UploadSide(ref _a, pair.A);
        UploadSide(ref _b, pair.B);
    }

    private void UploadSide(ref SourceTexture? source, MediaFrame? frame)
    {
        if (frame is null) { source?.Dispose(); source = null; return; }
        ValidateSize(frame.Width, frame.Height);
        if (source is null || source.Width != frame.Width || source.Height != frame.Height)
        {
            source?.Dispose(); source = null;
            source = SourceTexture.Create(_device!, frame.Width, frame.Height);
        }
        source.Upload(_device!, _context!, _adapterLuid, frame);
    }

    public void Render(PreviewView view)
    {
        Acquire(_outputMutex!, 0);
        try
        {
            var context = _context!;
            context.ClearRenderTargetView(_target!, Background);
            context.OMSetRenderTargets(_target!);
            context.RSSetState(_rasterizer);
            context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            context.VSSetShader(_vertexShader);
            context.PSSetShader(_pixelShader);
            context.PSSetConstantBuffer(0, _constants);
            context.PSSetSampler(0, view.Scale == PreviewScale.ActualPixels && view.Zoom >= 1 ? _pointSampler : _linearSampler);
            var half = Width / 2;
            Draw(_a, 0, half, view);
            Draw(_b, half, Width - half, view);
            context.PSUnsetShaderResource(0);
            context.UnsetRenderTargets();
            context.Flush();
        }
        finally { _outputMutex!.ReleaseSync(1); }
    }

    private void Draw(SourceTexture? source, int x, int width, PreviewView view)
    {
        if (source is null || width == 0) return;
        var (imageWidth, imageHeight) = view.ImageSize(source.Width, source.Height, width, Height);
        // UV = center + (viewportUV - .5) * viewportSize / imageSize.
        var constants = new Vector4((float)view.CenterX, (float)view.CenterY,
            (float)(width / imageWidth), (float)(Height / imageHeight));
        _context!.UpdateSubresource(in constants, _constants!);
        _context.RSSetViewport(new Viewport(x, 0, width, Height));
        _context.PSSetShaderResource(0, source.View);
        _context.Draw(3, 0);
    }

    internal static void ValidateSize(int width, int height)
    {
        if (width is < 1 or > 16384 || height is < 1 or > 16384)
            throw new NotSupportedException($"预览尺寸不受支持：{width} × {height}。");
    }

    private static Texture2DDescription TextureDescription(int width, int height, BindFlags bindings, bool shared = false) => new()
    {
        Width = (uint)width, Height = (uint)height, MipLevels = 1, ArraySize = 1,
        Format = Format.B8G8R8A8_UNorm, SampleDescription = new SampleDescription(1, 0),
        Usage = ResourceUsage.Default, BindFlags = shared ? BindFlags.RenderTarget | BindFlags.ShaderResource : bindings,
        MiscFlags = shared ? ResourceOptionFlags.SharedKeyedMutex : ResourceOptionFlags.None
    };

    private static unsafe void Acquire(IDXGIKeyedMutex mutex, ulong key)
    {
        // Vortice 3.8.3's void wrapper discards positive WAIT_TIMEOUT/WAIT_ABANDONED.
        // Use the documented IDXGIKeyedMutex COM ABI to preserve the HRESULT. Slot 8 follows
        // IUnknown (3), IDXGIObject (4), and IDXGIDeviceSubObject (1); no private API is involved.
        var native = mutex.NativePointer;
        var acquire = (delegate* unmanaged[Stdcall]<nint, ulong, uint, int>)(*(nint**)native)[8];
        var result = acquire(native, key, MutexTimeoutMs);
        GC.KeepAlive(mutex);
        if (result != 0) throw new IOException($"GPU 预览纹理同步失败（0x{result:X8}），请重新打开页面。");
    }

    private void ReleaseOutput()
    {
        _context?.UnsetRenderTargets();
        _target?.Dispose(); _target = null;
        _outputMutex?.Dispose(); _outputMutex = null;
        _output?.Dispose(); _output = null;
        SharedHandle = 0; Width = Height = 0;
    }

    public void Dispose()
    {
        _context?.ClearState();
        _a?.Dispose(); _a = null; _b?.Dispose(); _b = null;
        ReleaseOutput();
        _rasterizer?.Dispose(); _rasterizer = null;
        _pointSampler?.Dispose(); _pointSampler = null;
        _linearSampler?.Dispose(); _linearSampler = null;
        _constants?.Dispose(); _constants = null;
        _pixelShader?.Dispose(); _pixelShader = null;
        _vertexShader?.Dispose(); _vertexShader = null;
        _context?.Dispose(); _context = null;
        _device?.Dispose(); _device = null;
    }

    private sealed class SourceTexture : IDisposable
    {
        private ID3D11Texture2D? _texture;
        private ID3D11ShaderResourceView? _view;
        private ID3D11Device? _sourceDevice;
        private ID3D11DeviceContext? _sourceContext;
        private ID3D11Texture2D? _shared, _opened;
        private IDXGIKeyedMutex? _writeMutex, _readMutex;
        public int Width { get; private init; }
        public int Height { get; private init; }
        public ID3D11ShaderResourceView View => _view!;

        public static SourceTexture Create(ID3D11Device device, int width, int height)
        {
            var source = new SourceTexture { Width = width, Height = height };
            try
            {
                source._texture = device.CreateTexture2D(TextureDescription(width, height, BindFlags.ShaderResource));
                source._view = device.CreateShaderResourceView(source._texture);
                return source;
            }
            catch { source.Dispose(); throw; }
        }

        public unsafe void Upload(ID3D11Device device, ID3D11DeviceContext context, long adapterLuid, MediaFrame frame)
        {
            if (frame.Texture == 0)
            {
                if (frame.Stride < checked(Width * 4) || frame.Pixels.Length < checked(frame.Stride * Height))
                    throw new InvalidDataException("解码器返回的 BGRA 图像数据不完整。");
                using var pixels = frame.Pixels.Pin();
                context.UpdateSubresource(_texture!, 0, null, (nint)pixels.Pointer, (uint)frame.Stride, 0);
                return;
            }
            // MediaFrame owns the original COM reference. Vortice wrappers own their reference too.
            Marshal.AddRef(frame.Texture);
            using var texture = new ID3D11Texture2D(frame.Texture);
            var description = texture.Description;
            if (description.Format != Format.B8G8R8A8_UNorm || description.Width < Width || description.Height < Height ||
                description.SampleDescription.Count != 1 || description.MipLevels != 1 || frame.Subresource >= description.ArraySize)
                throw new InvalidDataException("解码器返回了不兼容的 GPU 纹理。");
            using var sourceDevice = texture.Device;
            if (_sourceDevice?.NativePointer != sourceDevice.NativePointer)
            {
                ReleaseShared();
                using var dxgi = sourceDevice.QueryInterface<IDXGIDevice>();
                using var adapter = dxgi.GetAdapter();
                if (adapter.Description.Luid != adapterLuid)
                    throw new NotSupportedException("媒体解码器与软件界面使用了不同显卡，当前无法共享预览；请将两者设为同一显卡。");
                try
                {
                    _sourceDevice = sourceDevice.QueryInterface<ID3D11Device>();
                    _sourceContext = _sourceDevice.ImmediateContext;
                    _shared = _sourceDevice.CreateTexture2D(TextureDescription(Width, Height, BindFlags.ShaderResource, shared: true));
                    _writeMutex = _shared.QueryInterface<IDXGIKeyedMutex>();
                    using var resource = _shared.QueryInterface<IDXGIResource>();
                    _opened = device.OpenSharedResource<ID3D11Texture2D>(resource.SharedHandle);
                    _readMutex = _opened.QueryInterface<IDXGIKeyedMutex>();
                }
                catch { ReleaseShared(); throw; }
            }
            if (frame.LockDevice is null || frame.UnlockDevice is null)
                throw new InvalidDataException("GPU 解码帧缺少设备同步接口。");
            Acquire(_writeMutex!, 0);
            try
            {
                frame.LockDevice();
                try
                {
                    _sourceContext!.CopySubresourceRegion(_shared!, 0, 0, 0, 0, texture, frame.Subresource,
                        new Box(0, 0, 0, Width, Height, 1));
                    _sourceContext.Flush();
                }
                finally { frame.UnlockDevice(); }
            }
            finally { _writeMutex!.ReleaseSync(1); }
            Acquire(_readMutex!, 1);
            try { context.CopyResource(_texture!, _opened!); context.Flush(); }
            finally { _readMutex!.ReleaseSync(0); }
        }

        private void ReleaseShared()
        {
            _readMutex?.Dispose(); _readMutex = null; _opened?.Dispose(); _opened = null;
            _writeMutex?.Dispose(); _writeMutex = null; _shared?.Dispose(); _shared = null;
            _sourceContext?.Dispose(); _sourceContext = null; _sourceDevice?.Dispose(); _sourceDevice = null;
        }

        public void Dispose()
        {
            ReleaseShared(); _view?.Dispose(); _view = null; _texture?.Dispose(); _texture = null;
        }
    }

    private const string Shader = """
        cbuffer View : register(b0) { float2 Center; float2 Extent; };
        Texture2D Image : register(t0);
        SamplerState ImageSampler : register(s0);
        struct Vertex { float4 Position : SV_POSITION; float2 Uv : TEXCOORD0; };
        Vertex VS(uint id : SV_VertexID) {
            Vertex v;
            v.Uv = float2((id << 1) & 2, id & 2);
            v.Position = float4(v.Uv.x * 2 - 1, 1 - v.Uv.y * 2, 0, 1);
            return v;
        }
        float4 PS(Vertex v) : SV_TARGET {
            float2 uv = Center + (v.Uv - .5) * Extent;
            float3 background = float3(.035, .035, .035);
            if (any(uv < 0) || any(uv > 1)) return float4(background, 1);
            float4 pixel = Image.Sample(ImageSampler, uv);
            return float4(lerp(background, pixel.rgb, pixel.a), 1);
        }
        """;
}
