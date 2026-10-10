using System.Numerics;
using System.Runtime.InteropServices;
using AnyCam.Core.VirtualCamera;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace AnyCam.Core.Media;

/// <summary>Color description of the incoming video.</summary>
public readonly record struct VideoColor(bool FullRange, bool Bt601)
{
    public static readonly VideoColor Bt709Video = new(false, false);
}

/// <summary>
/// The GPU pipeline: NV12 camera frame → "program" RGB image (framing, color, LUT) → outputs
/// (virtual camera NV12/BGRA readback, preview with monitoring overlays). All methods must be
/// called on the media engine thread.
/// </summary>
public sealed unsafe class FrameProcessor : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct DevelopConstants
    {
        public Vector4 UvRow0;
        public Vector4 UvRow1;
        public Vector4 YuvRange;
        public Vector4 Adjust0;
        public Vector4 Adjust1;
        public Vector4 WhiteBalance;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct OutputConstants
    {
        public Vector4 Map;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PreviewConstants
    {
        public Vector4 Map;
        public Vector4 Flags;
        public Vector4 Params;
        public Vector4 Size;
    }

    private readonly GpuDevice _gpu;
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly ID3D11VertexShader _vsFullscreen;
    private readonly ID3D11PixelShader _psDevelop;
    private readonly ID3D11PixelShader _psSlate;
    private readonly ID3D11PixelShader _psLuma;
    private readonly ID3D11PixelShader _psChroma;
    private readonly ID3D11PixelShader _psRgba;
    private readonly ID3D11PixelShader _psPreview;
    private readonly ID3D11SamplerState _linearClamp;
    private readonly ID3D11Buffer _developBuffer;
    private readonly ID3D11Buffer _outputBuffer;
    private readonly ID3D11Buffer _previewBuffer;
    private readonly long _startTicks = Environment.TickCount64;

    // Input (camera) NV12 texture and its plane views.
    private ID3D11Texture2D? _input;
    private ID3D11Texture2D? _inputStaging; // software-decoder upload path
    private ID3D11ShaderResourceView? _inputLuma;
    private ID3D11ShaderResourceView? _inputChroma;
    private int _inputWidth;
    private int _inputHeight;

    // Slate shown when no camera is connected.
    private ID3D11Texture2D? _slate;
    private ID3D11ShaderResourceView? _slateView;
    private int _slateWidth;
    private int _slateHeight;

    // Program (processed RGB) image.
    private ID3D11Texture2D? _program;
    private ID3D11RenderTargetView? _programTarget;
    private ID3D11ShaderResourceView? _programView;

    // 3D LUT.
    private ID3D11Texture3D? _lut;
    private ID3D11ShaderResourceView? _lutView;
    private int _lutSize;

    // Virtual camera render targets + staging copies.
    private ID3D11Texture2D? _vcamLuma, _vcamChroma, _vcamRgba;
    private ID3D11RenderTargetView? _vcamLumaTarget, _vcamChromaTarget, _vcamRgbaTarget;
    private ID3D11Texture2D? _vcamLumaStaging, _vcamChromaStaging, _vcamRgbaStaging;
    private int _vcamWidth, _vcamHeight;
    private VCamPixelFormat _vcamFormat;

    // Last develop mapping (program uv -> source uv), used for click-to-focus.
    private Matrix3x2 _programToSource = Matrix3x2.Identity;

    public FrameProcessor(GpuDevice gpu)
    {
        _gpu = gpu;
        _device = gpu.Device;
        _context = gpu.Context;

        _vsFullscreen = _device.CreateVertexShader(Compile("VSFullscreen", "vs_5_0").Span);
        _psDevelop = _device.CreatePixelShader(Compile("PSDevelop", "ps_5_0").Span);
        _psSlate = _device.CreatePixelShader(Compile("PSSlate", "ps_5_0").Span);
        _psLuma = _device.CreatePixelShader(Compile("PSLuma", "ps_5_0").Span);
        _psChroma = _device.CreatePixelShader(Compile("PSChroma", "ps_5_0").Span);
        _psRgba = _device.CreatePixelShader(Compile("PSRgba", "ps_5_0").Span);
        _psPreview = _device.CreatePixelShader(Compile("PSPreview", "ps_5_0").Span);

        _linearClamp = _device.CreateSamplerState(new SamplerDescription(Filter.MinMagMipLinear, TextureAddressMode.Clamp, TextureAddressMode.Clamp, TextureAddressMode.Clamp));
        _developBuffer = CreateConstantBuffer<DevelopConstants>();
        _outputBuffer = CreateConstantBuffer<OutputConstants>();
        _previewBuffer = CreateConstantBuffer<PreviewConstants>();
    }

    public int ProgramWidth { get; private set; }
    public int ProgramHeight { get; private set; }
    public bool HasInput => _input is not null;
    public bool HasProgram => _program is not null;

    private static ReadOnlyMemory<byte> Compile(string entryPoint, string profile) =>
        Compiler.Compile(Shaders.Source, entryPoint, "anycam.hlsl", profile, ShaderFlags.OptimizationLevel3, EffectFlags.None);

    private ID3D11Buffer CreateConstantBuffer<T>() where T : unmanaged =>
        _device.CreateBuffer(new BufferDescription((uint)((sizeof(T) + 15) & ~15), BindFlags.ConstantBuffer, ResourceUsage.Default));

    // ---------------------------------------------------------------------------------------
    // Input

    /// <summary>Copies a decoded picture into the pipeline's NV12 input texture.</summary>
    public void Upload(in DecodedFrame frame)
    {
        EnsureInput(frame.Width & ~1, frame.Height & ~1);

        if (frame.Texture is not null)
        {
            var box = new Box(0, 0, 0, _inputWidth, _inputHeight, 1);
            _context.CopySubresourceRegion(_input!, 0, 0, 0, 0, frame.Texture, (uint)frame.Subresource, box);
            return;
        }

        // Software decode: write both planes into a staging texture, then copy on the GPU.
        _inputStaging ??= _device.CreateTexture2D(new Texture2DDescription(Format.NV12, (uint)_inputWidth, (uint)_inputHeight, 1, 1,
            BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Write, 1, 0, ResourceOptionFlags.None));
        var mapped = _context.Map(_inputStaging, 0, MapMode.Write, Vortice.Direct3D11.MapFlags.None);
        try
        {
            var source = (byte*)frame.CpuData;
            var destination = (byte*)mapped.DataPointer;
            for (var y = 0; y < _inputHeight; y++)
                Buffer.MemoryCopy(source + (long)y * frame.CpuPitch, destination + (long)y * mapped.RowPitch, mapped.RowPitch, _inputWidth);
            var sourceChroma = source + (long)frame.CpuPitch * frame.CpuSurfaceHeight;
            var destinationChroma = destination + (long)mapped.RowPitch * _inputHeight;
            for (var y = 0; y < _inputHeight / 2; y++)
                Buffer.MemoryCopy(sourceChroma + (long)y * frame.CpuPitch, destinationChroma + (long)y * mapped.RowPitch, mapped.RowPitch, _inputWidth);
        }
        finally
        {
            _context.Unmap(_inputStaging, 0);
        }
        _context.CopyResource(_input!, _inputStaging);
    }

    private void EnsureInput(int width, int height)
    {
        if (_input is not null && width == _inputWidth && height == _inputHeight)
            return;

        DisposeInput();
        _inputWidth = width;
        _inputHeight = height;
        _input = _device.CreateTexture2D(new Texture2DDescription(Format.NV12, (uint)width, (uint)height, 1, 1,
            BindFlags.ShaderResource, ResourceUsage.Default, CpuAccessFlags.None, 1, 0, ResourceOptionFlags.None));
        _inputLuma = _device.CreateShaderResourceView(_input, new ShaderResourceViewDescription(_input, ShaderResourceViewDimension.Texture2D, Format.R8_UNorm, 0, 1, 0, 1));
        _inputChroma = _device.CreateShaderResourceView(_input, new ShaderResourceViewDescription(_input, ShaderResourceViewDimension.Texture2D, Format.R8G8_UNorm, 0, 1, 0, 1));
    }

    private void DisposeInput()
    {
        _inputLuma?.Dispose();
        _inputChroma?.Dispose();
        _input?.Dispose();
        _inputStaging?.Dispose();
        _inputLuma = _inputChroma = null;
        _input = _inputStaging = null;
    }

    /// <summary>Forgets the current camera picture (e.g. on disconnect) so the slate is shown.</summary>
    public void ClearInput() => DisposeInput();

    /// <summary>Sets the image shown while no camera is connected (top-down BGRA).</summary>
    public void SetSlate(byte[] bgra, int width, int height)
    {
        _slateView?.Dispose();
        _slate?.Dispose();
        fixed (byte* pixels = bgra)
        {
            var data = new SubresourceData(pixels, (uint)(width * 4));
            var description = new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)width, (uint)height, 1, 1,
                BindFlags.ShaderResource, ResourceUsage.Immutable, CpuAccessFlags.None, 1, 0, ResourceOptionFlags.None);
            _slate = _device.CreateTexture2D(in description, data);
        }
        _slateView = _device.CreateShaderResourceView(_slate, null);
        _slateWidth = width;
        _slateHeight = height;
    }

    public bool HasSlate => _slate is not null;

    public void SetLut(CubeLut? lut)
    {
        _lutView?.Dispose();
        _lut?.Dispose();
        _lutView = null;
        _lut = null;
        _lutSize = 0;
        if (lut is null)
            return;

        fixed (float* values = lut.Data)
        {
            var size = (uint)lut.Size;
            var data = new SubresourceData(values, size * 16, size * size * 16);
            var description = new Texture3DDescription(Format.R32G32B32A32_Float, size, size, size, 1,
                BindFlags.ShaderResource, ResourceUsage.Immutable, CpuAccessFlags.None, ResourceOptionFlags.None);
            _lut = _device.CreateTexture3D(in description, data);
        }
        _lutView = _device.CreateShaderResourceView(_lut, null);
        _lutSize = lut.Size;
    }

    // ---------------------------------------------------------------------------------------
    // Develop

    /// <summary>Renders the program image from the camera input (or the slate when there is none).</summary>
    public void Develop(ProcessingSettings settings, VideoColor color)
    {
        var useSlate = _input is null;
        if (useSlate && _slate is null)
            return;

        var sourceWidth = useSlate ? _slateWidth : _inputWidth;
        var sourceHeight = useSlate ? _slateHeight : _inputHeight;
        var rotation = useSlate ? 0 : ((settings.Rotation % 360) + 360) % 360;
        var rotatedWidth = rotation is 90 or 270 ? sourceHeight : sourceWidth;
        var rotatedHeight = rotation is 90 or 270 ? sourceWidth : sourceHeight;

        var programWidth = settings.OutputWidth > 0 ? settings.OutputWidth : rotatedWidth;
        var programHeight = settings.OutputHeight > 0 ? settings.OutputHeight : rotatedHeight;
        if (useSlate)
        {
            // Keep the program size of the last video so outputs don't jump around.
            programWidth = ProgramWidth > 0 ? ProgramWidth : programWidth;
            programHeight = ProgramHeight > 0 ? ProgramHeight : programHeight;
        }
        EnsureProgram(programWidth & ~1, programHeight & ~1);

        var mapping = useSlate
            ? FitMapping(programWidth, programHeight, sourceWidth, sourceHeight, fill: true, 1, 0, 0, false, false, 0)
            : FitMapping(programWidth, programHeight, rotatedWidth, rotatedHeight, settings.Fill, settings.Zoom,
                settings.PanX, settings.PanY, settings.Mirror, settings.Flip, rotation);
        _programToSource = mapping;

        var constants = new DevelopConstants
        {
            UvRow0 = new Vector4(mapping.M11, mapping.M21, mapping.M31, 0),
            UvRow1 = new Vector4(mapping.M12, mapping.M22, mapping.M32, 0),
            YuvRange = color.FullRange ? new Vector4(0, 1, 1, color.Bt601 ? 1 : 0) : new Vector4(16f / 255, 255f / 219, 255f / 224, color.Bt601 ? 1 : 0),
            Adjust0 = new Vector4((float)Math.Pow(2, settings.Exposure), (float)settings.Contrast, (float)settings.Saturation, (float)Math.Max(settings.Gamma, 0.1)),
            Adjust1 = new Vector4((float)settings.Brightness, _lutView is null ? 0 : (float)Math.Clamp(settings.LutAmount, 0, 1), _lutSize, (float)settings.Vibrance),
            WhiteBalance = WhiteBalanceGains(settings.Temperature, settings.Tint),
        };
        _context.UpdateSubresource(in constants, _developBuffer);

        _context.OMSetRenderTargets(_programTarget!, null);
        _context.RSSetViewport(0, 0, ProgramWidth, ProgramHeight, 0, 1);
        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _context.IASetInputLayout(null);
        _context.VSSetShader(_vsFullscreen);
        _context.PSSetConstantBuffer(0, _developBuffer);
        _context.PSSetSampler(0, _linearClamp);
        if (useSlate)
        {
            _context.PSSetShader(_psSlate);
            _context.PSSetShaderResource(0, _slateView!);
        }
        else
        {
            _context.PSSetShader(_psDevelop);
            _context.PSSetShaderResource(0, _inputLuma!);
            _context.PSSetShaderResource(1, _inputChroma!);
            if (_lutView is not null)
                _context.PSSetShaderResource(2, _lutView);
        }
        _context.Draw(3, 0);
        UnbindShaderResources(3);

        // Mips give clean downscaling for smaller outputs and the half-resolution chroma pass.
        _context.GenerateMips(_programView!);
    }

    private static Vector4 WhiteBalanceGains(double temperature, double tint) =>
        new((float)(1 + 0.22 * temperature), (float)(1 - 0.18 * tint), (float)(1 - 0.22 * temperature), 1);

    private void EnsureProgram(int width, int height)
    {
        if (_program is not null && width == ProgramWidth && height == ProgramHeight)
            return;

        _programView?.Dispose();
        _programTarget?.Dispose();
        _program?.Dispose();
        ProgramWidth = width;
        ProgramHeight = height;
        _program = _device.CreateTexture2D(new Texture2DDescription(Format.R8G8B8A8_UNorm, (uint)width, (uint)height, 1, 0,
            BindFlags.RenderTarget | BindFlags.ShaderResource, ResourceUsage.Default, CpuAccessFlags.None, 1, 0, ResourceOptionFlags.GenerateMips));
        _programTarget = _device.CreateRenderTargetView(_program, new RenderTargetViewDescription(_program, RenderTargetViewDimension.Texture2D, Format.R8G8B8A8_UNorm, 0, 0, 1));
        _programView = _device.CreateShaderResourceView(_program, null);
    }

    /// <summary>
    /// Builds the affine map from program uv to source uv: fit/fill, digital zoom + pan, mirror/flip
    /// (applied to the upright image) and the inverse of the clockwise rotation.
    /// </summary>
    internal static Matrix3x2 FitMapping(int targetWidth, int targetHeight, int uprightWidth, int uprightHeight,
        bool fill, double zoom, double panX, double panY, bool mirror, bool flip, int rotation)
    {
        var targetAspect = (double)targetWidth / Math.Max(targetHeight, 1);
        var sourceAspect = (double)uprightWidth / Math.Max(uprightHeight, 1);
        double fractionX = 1, fractionY = 1;
        if (fill == targetAspect > sourceAspect)
            fractionY = sourceAspect / targetAspect; // fill: crop top/bottom; fit: letterbox sides
        else
            fractionX = targetAspect / sourceAspect;
        if (fill)
        {
            // In fill mode the visible fraction is <= 1 on both axes; fit mode may exceed 1 (letterbox).
            fractionX = Math.Min(fractionX, 1);
            fractionY = Math.Min(fractionY, 1);
        }
        else
        {
            fractionX = Math.Max(fractionX, 1);
            fractionY = Math.Max(fractionY, 1);
        }

        zoom = Math.Max(zoom, 1);
        fractionX /= zoom;
        fractionY /= zoom;
        var centerX = 0.5 + Math.Clamp(panX, -1, 1) * Math.Max(0, 1 - fractionX) / 2;
        var centerY = 0.5 + Math.Clamp(panY, -1, 1) * Math.Max(0, 1 - fractionY) / 2;

        Vector2 Map(double u, double v)
        {
            var x = centerX + (u - 0.5) * fractionX;
            var y = centerY + (v - 0.5) * fractionY;
            if (mirror) x = 1 - x;
            if (flip) y = 1 - y;
            return rotation switch
            {
                90 => new Vector2((float)y, (float)(1 - x)),
                180 => new Vector2((float)(1 - x), (float)(1 - y)),
                270 => new Vector2((float)(1 - y), (float)x),
                _ => new Vector2((float)x, (float)y),
            };
        }

        var origin = Map(0, 0);
        var du = Map(1, 0) - origin;
        var dv = Map(0, 1) - origin;
        return new Matrix3x2(du.X, du.Y, dv.X, dv.Y, origin.X, origin.Y);
    }

    /// <summary>Scale/offset that maps an output of the given size onto the program image (fit or fill).</summary>
    private Vector4 OutputMap(int width, int height, bool fill)
    {
        var outputAspect = (double)width / Math.Max(height, 1);
        var programAspect = (double)ProgramWidth / Math.Max(ProgramHeight, 1);
        double scaleX = 1, scaleY = 1;
        if (fill == outputAspect > programAspect)
            scaleY = programAspect / outputAspect;
        else
            scaleX = outputAspect / programAspect;
        return new Vector4((float)scaleX, (float)scaleY, (float)(0.5 - scaleX / 2), (float)(0.5 - scaleY / 2));
    }

    /// <summary>Maps a program-image point (0..1) to the matching point in the camera image (0..1).</summary>
    public Vector2 ProgramToSource(Vector2 programUv) => Vector2.Transform(programUv, _programToSource);

    // ---------------------------------------------------------------------------------------
    // Virtual camera output

    /// <summary>Renders the program image at the size/format the virtual camera client asked for and copies it into shared memory.</summary>
    public bool WriteVirtualCamera(VirtualCameraOutput output, VCamRequest request, bool fill, long timestamp100ns)
    {
        if (_programView is null)
            return false;

        var width = request.Width & ~1;
        var height = request.Height & ~1;
        EnsureVirtualCameraTargets(width, height, request.Format);

        var constants = new OutputConstants { Map = OutputMap(width, height, fill) };
        _context.UpdateSubresource(in constants, _outputBuffer);
        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _context.IASetInputLayout(null);
        _context.VSSetShader(_vsFullscreen);
        _context.PSSetConstantBuffer(0, _outputBuffer);
        _context.PSSetSampler(0, _linearClamp);
        _context.PSSetShaderResource(0, _programView);

        if (request.Format == VCamPixelFormat.Bgra)
        {
            DrawTo(_vcamRgbaTarget!, _psRgba, width, height);
            UnbindShaderResources(1);
            _context.CopyResource(_vcamRgbaStaging!, _vcamRgba!);
        }
        else
        {
            DrawTo(_vcamLumaTarget!, _psLuma, width, height);
            DrawTo(_vcamChromaTarget!, _psChroma, width / 2, height / 2);
            UnbindShaderResources(1);
            _context.CopyResource(_vcamLumaStaging!, _vcamLuma!);
            _context.CopyResource(_vcamChromaStaging!, _vcamChroma!);
        }

        if (!output.TryBeginFrame(width, height, request.Format, out var destination, out var stride))
            return false;
        try
        {
            if (request.Format == VCamPixelFormat.Bgra)
            {
                CopyRows(_vcamRgbaStaging!, destination, stride, height, width * 4);
            }
            else
            {
                CopyRows(_vcamLumaStaging!, destination, stride, height, width);
                CopyRows(_vcamChromaStaging!, destination + (long)stride * height, stride, height / 2, width);
            }
        }
        catch
        {
            output.AbortFrame();
            throw;
        }
        output.CommitFrame(width, height, request.Format, timestamp100ns);
        return true;
    }

    private void DrawTo(ID3D11RenderTargetView target, ID3D11PixelShader shader, int width, int height)
    {
        _context.OMSetRenderTargets(target, null);
        _context.RSSetViewport(0, 0, width, height, 0, 1);
        _context.PSSetShader(shader);
        _context.Draw(3, 0);
    }

    private void CopyRows(ID3D11Texture2D staging, byte* destination, int destinationStride, int rows, int rowBytes)
    {
        var mapped = _context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            var source = (byte*)mapped.DataPointer;
            for (var y = 0; y < rows; y++)
                Buffer.MemoryCopy(source + (long)y * mapped.RowPitch, destination + (long)y * destinationStride, destinationStride, rowBytes);
        }
        finally
        {
            _context.Unmap(staging, 0);
        }
    }

    private void EnsureVirtualCameraTargets(int width, int height, VCamPixelFormat format)
    {
        if (width == _vcamWidth && height == _vcamHeight && format == _vcamFormat)
            return;
        DisposeVirtualCameraTargets();
        _vcamWidth = width;
        _vcamHeight = height;
        _vcamFormat = format;

        if (format == VCamPixelFormat.Bgra)
        {
            (_vcamRgba, _vcamRgbaTarget, _vcamRgbaStaging) = CreateTarget(Format.B8G8R8A8_UNorm, width, height);
        }
        else
        {
            (_vcamLuma, _vcamLumaTarget, _vcamLumaStaging) = CreateTarget(Format.R8_UNorm, width, height);
            (_vcamChroma, _vcamChromaTarget, _vcamChromaStaging) = CreateTarget(Format.R8G8_UNorm, width / 2, height / 2);
        }
    }

    private (ID3D11Texture2D, ID3D11RenderTargetView, ID3D11Texture2D) CreateTarget(Format format, int width, int height)
    {
        var texture = _device.CreateTexture2D(new Texture2DDescription(format, (uint)width, (uint)height, 1, 1,
            BindFlags.RenderTarget, ResourceUsage.Default, CpuAccessFlags.None, 1, 0, ResourceOptionFlags.None));
        var target = _device.CreateRenderTargetView(texture, null);
        var staging = _device.CreateTexture2D(new Texture2DDescription(format, (uint)width, (uint)height, 1, 1,
            BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read, 1, 0, ResourceOptionFlags.None));
        return (texture, target, staging);
    }

    private void DisposeVirtualCameraTargets()
    {
        foreach (var resource in new IDisposable?[]
                 {
                     _vcamLumaTarget, _vcamChromaTarget, _vcamRgbaTarget, _vcamLuma, _vcamChroma, _vcamRgba,
                     _vcamLumaStaging, _vcamChromaStaging, _vcamRgbaStaging,
                 })
            resource?.Dispose();
        _vcamLumaTarget = _vcamChromaTarget = _vcamRgbaTarget = null;
        _vcamLuma = _vcamChroma = _vcamRgba = _vcamLumaStaging = _vcamChromaStaging = _vcamRgbaStaging = null;
        _vcamWidth = _vcamHeight = 0;
        _vcamFormat = VCamPixelFormat.None;
    }

    // ---------------------------------------------------------------------------------------
    // Preview

    /// <summary>Draws the program image with monitoring overlays into a preview swap chain.</summary>
    public void RenderPreview(PreviewSurface surface, ProcessingSettings settings)
    {
        if (!surface.BeginFrame(out var target, out var width, out var height))
            return;

        _context.OMSetRenderTargets(target, null);
        _context.RSSetViewport(0, 0, width, height, 0, 1);
        if (_programView is null)
        {
            _context.ClearRenderTargetView(target, new Color4(0.075f, 0.075f, 0.085f, 1));
            surface.Present();
            return;
        }

        var map = PreviewMap(width, height);
        var constants = new PreviewConstants
        {
            Map = map,
            Flags = new Vector4(settings.ShowGrid ? 1 : 0, settings.ShowZebras ? 1 : 0, settings.ShowPeaking ? 1 : 0, settings.ShowSafeAreas ? 1 : 0),
            Params = new Vector4((float)settings.ZebraThreshold, (float)settings.PeakingThreshold,
                (Environment.TickCount64 - _startTicks) / 1000f, settings.ShowVerticalGuide ? 1 : 0),
            Size = new Vector4(ProgramWidth, ProgramHeight, 1f / ProgramWidth, 1f / ProgramHeight),
        };
        _context.UpdateSubresource(in constants, _previewBuffer);
        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _context.IASetInputLayout(null);
        _context.VSSetShader(_vsFullscreen);
        _context.PSSetShader(_psPreview);
        _context.PSSetConstantBuffer(0, _previewBuffer);
        _context.PSSetSampler(0, _linearClamp);
        _context.PSSetShaderResource(0, _programView);
        _context.Draw(3, 0);
        UnbindShaderResources(1);
        surface.Present();
    }

    /// <summary>Letterboxed mapping of the program into a viewport (preview uv → program uv).</summary>
    public Vector4 PreviewMap(int viewportWidth, int viewportHeight)
    {
        if (ProgramWidth == 0 || ProgramHeight == 0)
            return new Vector4(1, 1, 0, 0);
        var viewportAspect = (double)viewportWidth / Math.Max(viewportHeight, 1);
        var programAspect = (double)ProgramWidth / ProgramHeight;
        double scaleX = 1, scaleY = 1;
        if (viewportAspect > programAspect)
            scaleX = viewportAspect / programAspect;
        else
            scaleY = programAspect / viewportAspect;
        return new Vector4((float)scaleX, (float)scaleY, (float)(0.5 - scaleX / 2), (float)(0.5 - scaleY / 2));
    }

    // ---------------------------------------------------------------------------------------
    // Snapshot

    /// <summary>Reads back the program image as top-down BGRA (for saving stills of the processed output).</summary>
    public (byte[] Pixels, int Width, int Height)? ReadProgram()
    {
        if (_program is null)
            return null;
        var (texture, target, staging) = CreateTarget(Format.B8G8R8A8_UNorm, ProgramWidth, ProgramHeight);
        using (texture)
        using (target)
        using (staging)
        {
            var constants = new OutputConstants { Map = new Vector4(1, 1, 0, 0) };
            _context.UpdateSubresource(in constants, _outputBuffer);
            _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            _context.IASetInputLayout(null);
            _context.VSSetShader(_vsFullscreen);
            _context.PSSetConstantBuffer(0, _outputBuffer);
            _context.PSSetSampler(0, _linearClamp);
            _context.PSSetShaderResource(0, _programView!);
            DrawTo(target, _psRgba, ProgramWidth, ProgramHeight);
            UnbindShaderResources(1);
            _context.CopyResource(staging, texture);

            var pixels = new byte[ProgramWidth * ProgramHeight * 4];
            fixed (byte* destination = pixels)
                CopyRows(staging, destination, ProgramWidth * 4, ProgramHeight, ProgramWidth * 4);
            return (pixels, ProgramWidth, ProgramHeight);
        }
    }

    private void UnbindShaderResources(int count)
    {
        for (uint slot = 0; slot < count; slot++)
            _context.PSSetShaderResource(slot, null!);
        _context.OMSetRenderTargets((ID3D11RenderTargetView)null!, null);
    }

    public void Dispose()
    {
        DisposeVirtualCameraTargets();
        DisposeInput();
        _slateView?.Dispose();
        _slate?.Dispose();
        _lutView?.Dispose();
        _lut?.Dispose();
        _programView?.Dispose();
        _programTarget?.Dispose();
        _program?.Dispose();
        _developBuffer.Dispose();
        _outputBuffer.Dispose();
        _previewBuffer.Dispose();
        _linearClamp.Dispose();
        _vsFullscreen.Dispose();
        _psDevelop.Dispose();
        _psSlate.Dispose();
        _psLuma.Dispose();
        _psChroma.Dispose();
        _psRgba.Dispose();
        _psPreview.Dispose();
    }
}
