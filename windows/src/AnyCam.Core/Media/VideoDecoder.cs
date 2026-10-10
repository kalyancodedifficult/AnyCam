using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.MediaFoundation;

namespace AnyCam.Core.Media;

/// <summary>A decoded NV12 picture: a GPU surface (hardware decode) or CPU memory (software decode).</summary>
public readonly ref struct DecodedFrame
{
    public DecodedFrame(ID3D11Texture2D texture, int subresource, int width, int height, long timestamp)
    {
        Texture = texture;
        Subresource = subresource;
        Width = width;
        Height = height;
        Timestamp = timestamp;
        CpuData = IntPtr.Zero;
        CpuPitch = 0;
        CpuSurfaceHeight = 0;
    }

    public DecodedFrame(IntPtr data, int pitch, int surfaceHeight, int width, int height, long timestamp)
    {
        Texture = null;
        Subresource = 0;
        CpuData = data;
        CpuPitch = pitch;
        CpuSurfaceHeight = surfaceHeight;
        Width = width;
        Height = height;
        Timestamp = timestamp;
    }

    public ID3D11Texture2D? Texture { get; }
    public int Subresource { get; }
    public IntPtr CpuData { get; }
    public int CpuPitch { get; }

    /// <summary>Rows of the luma plane in memory (the chroma plane starts at CpuPitch * CpuSurfaceHeight).</summary>
    public int CpuSurfaceHeight { get; }
    public int Width { get; }
    public int Height { get; }

    /// <summary>Presentation time in 100 ns units (camera clock).</summary>
    public long Timestamp { get; }
}

public delegate void DecodedFrameHandler(in DecodedFrame frame);

/// <summary>
/// H.264 / HEVC decoder built on the Media Foundation decoder MFT, DXVA-accelerated on our D3D11 device
/// and configured for low latency (no output reordering delay).
/// </summary>
public sealed class VideoDecoder : IDisposable
{
    private readonly IMFTransform _transform;
    private readonly bool _hevc;
    private bool _useD3D;
    private bool _providesSamples;
    private int _outputBufferSize;
    private int _width;
    private int _height;
    private int _surfaceHeight;
    private int _cpuPitch;

    public VideoDecoder(GpuDevice gpu, bool hevc, int width, int height, double fps)
    {
        MediaPlatform.EnsureStarted();
        _hevc = hevc;
        _width = width;
        _height = height;
        _surfaceHeight = height;

        (_transform, Name) = CreateTransform(hevc);

        using (var attributes = _transform.Attributes)
        {
            attributes.Set(MfConstants.LowLatency, 1u);
            if (gpu.SupportsVideo && attributes.GetUInt32(TransformAttributeKeys.D3D11Aware, out var aware).Success && aware != 0)
            {
                try
                {
                    _transform.ProcessMessage(TMessageType.MessageSetD3DManager, (UIntPtr)(ulong)gpu.DeviceManager.NativePointer);
                    _useD3D = true;
                }
                catch (SharpGenException)
                {
                    _useD3D = false; // falls back to software decoding
                }
            }
        }

        using (var input = MediaFactory.MFCreateMediaType())
        {
            input.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            input.Set(MediaTypeAttributeKeys.Subtype, hevc ? VideoFormatGuids.Hevc : VideoFormatGuids.H264);
            input.Set(MediaTypeAttributeKeys.FrameSize, MfConstants.PackSize(width, height));
            var (num, den) = MfConstants.FpsRatio(fps);
            input.Set(MediaTypeAttributeKeys.FrameRate, MfConstants.PackRatio(num, den));
            input.Set(MediaTypeAttributeKeys.InterlaceMode, (uint)VideoInterlaceMode.Progressive);
            _transform.SetInputType(0, input, 0);
        }

        SelectNv12Output();
        _transform.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
        _transform.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);
    }

    public string Name { get; }
    public bool IsHardware => _useD3D;
    public int Width => _width;
    public int Height => _height;

    private static (IMFTransform Transform, string Name) CreateTransform(bool hevc)
    {
        var input = new RegisterTypeInfo
        {
            GuidMajorType = MediaTypeGuids.Video,
            GuidSubtype = hevc ? VideoFormatGuids.Hevc : VideoFormatGuids.H264,
        };
        var flags = MfConstants.EnumSyncMft | MfConstants.EnumLocalMft | MfConstants.EnumSortAndFilter;
        using var activates = MediaFactory.MFTEnumEx(TransformCategoryGuids.VideoDecoder, flags, input, null);
        Exception? lastError = null;
        foreach (var activate in activates)
        {
            try
            {
                string name;
                try
                {
                    name = activate.GetString(TransformAttributeKeys.MftFriendlyNameAttribute);
                }
                catch (SharpGenException)
                {
                    name = hevc ? "HEVC decoder" : "H.264 decoder";
                }
                var transform = activate.ActivateObject<IMFTransform>();
                return (transform, name);
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        var codec = hevc ? "HEVC" : "H.264";
        throw new NotSupportedException(
            hevc
                ? "No HEVC decoder is installed. Install \"HEVC Video Extensions\" from the Microsoft Store or switch the phone to H.264."
                : $"No {codec} decoder is available on this PC.",
            lastError);
    }

    private void SelectNv12Output()
    {
        for (var index = 0; ; index++)
        {
            IMFMediaType type;
            try
            {
                type = _transform.GetOutputAvailableType(0, index);
            }
            catch (SharpGenException ex) when (ex.ResultCode.Code == MfConstants.NoMoreTypes)
            {
                throw new NotSupportedException("The decoder can't output NV12.");
            }

            using (type)
            {
                if (type.GetGUID(MediaTypeAttributeKeys.Subtype) != VideoFormatGuids.NV12)
                    continue;

                _transform.SetOutputType(0, type, 0);
                ReadOutputGeometry(type);
                break;
            }
        }

        var info = _transform.GetOutputStreamInfo(0);
        _providesSamples = (info.Flags & (MfConstants.OutputProvidesSamples | MfConstants.OutputCanProvideSamples)) != 0;
        _outputBufferSize = info.Size;
    }

    private void ReadOutputGeometry(IMFMediaType type)
    {
        var (surfaceWidth, surfaceHeight) = MfConstants.UnpackSize(type.GetUInt64(MediaTypeAttributeKeys.FrameSize));
        _surfaceHeight = surfaceHeight;
        _cpuPitch = surfaceWidth;
        if (type.GetUInt32(MediaTypeAttributeKeys.DefaultStride, out var stride).Success && stride > 0)
            _cpuPitch = (int)stride;

        // Visible area (the surface may be padded to a multiple of 16, e.g. 1920x1088).
        var blob = type.GetBlobSize(MfConstants.MinimumDisplayAperture, out var size).Success && size >= 16
            ? type.GetBlob(MfConstants.MinimumDisplayAperture)
            : null;
        if (blob is { Length: >= 16 })
        {
            // MFVideoArea: MFOffset x, MFOffset y (fixed point 16.16), SIZE (cx, cy)
            _width = BitConverter.ToInt32(blob, 8);
            _height = BitConverter.ToInt32(blob, 12);
        }
        else
        {
            _width = Math.Min(_width > 0 ? _width : surfaceWidth, surfaceWidth);
            _height = Math.Min(_height > 0 ? _height : surfaceHeight, surfaceHeight);
        }
    }

    /// <summary>Decodes one access unit. Decoded pictures are passed to <paramref name="onFrame"/> synchronously.</summary>
    public void Decode(ReadOnlySpan<byte> accessUnit, long timestamp100ns, DecodedFrameHandler onFrame)
    {
        using var sample = MediaFactory.MFCreateSample();
        using var buffer = MediaFactory.MFCreateMemoryBuffer(accessUnit.Length);
        buffer.Lock(out var pointer, out _, out _);
        unsafe
        {
            accessUnit.CopyTo(new Span<byte>((void*)pointer, accessUnit.Length));
        }
        buffer.Unlock();
        buffer.CurrentLength = accessUnit.Length;
        sample.AddBuffer(buffer);
        sample.SampleTime = timestamp100ns;

        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                _transform.ProcessInput(0, sample, 0);
                break;
            }
            catch (SharpGenException ex) when (ex.ResultCode.Code == MfConstants.NotAccepting && attempt == 0)
            {
                Drain(onFrame); // make room, then retry once
            }
        }
        Drain(onFrame);
    }

    /// <summary>Discards everything queued in the decoder (e.g. after a gap in the stream).</summary>
    public void Flush() => _transform.ProcessMessage(TMessageType.MessageCommandFlush, UIntPtr.Zero);

    private void Drain(DecodedFrameHandler onFrame)
    {
        for (var guard = 0; guard < 16; guard++)
        {
            IMFSample? provided = null;
            if (!_providesSamples)
            {
                provided = MediaFactory.MFCreateSample();
                using var outBuffer = MediaFactory.MFCreateMemoryBuffer(Math.Max(_outputBufferSize, _cpuPitch * _surfaceHeight * 3 / 2));
                provided.AddBuffer(outBuffer);
            }

            var output = new OutputDataBuffer { StreamID = 0, Sample = provided! };
            var result = _transform.ProcessOutput(ProcessOutputFlags.None, 1, ref output, out _);
            output.Events?.Dispose();

            if (result.Code == MfConstants.NeedMoreInput)
            {
                provided?.Dispose();
                return;
            }
            if (result.Code == MfConstants.StreamChange)
            {
                provided?.Dispose();
                SelectNv12Output();
                continue;
            }
            if (result.Failure)
            {
                provided?.Dispose();
                throw new SharpGenException(result);
            }

            var sample = output.Sample ?? provided;
            try
            {
                if (sample is not null)
                    Deliver(sample, onFrame);
            }
            finally
            {
                if (output.Sample is not null && !ReferenceEquals(output.Sample, provided))
                    output.Sample.Dispose();
                provided?.Dispose();
            }
        }
    }

    private void Deliver(IMFSample sample, DecodedFrameHandler onFrame)
    {
        var timestamp = sample.SampleTime;
        using var buffer = sample.GetBufferByIndex(0);

        if (_useD3D)
        {
            IMFDXGIBuffer? dxgiBuffer = null;
            try
            {
                dxgiBuffer = buffer.QueryInterfaceOrNull<IMFDXGIBuffer>();
                if (dxgiBuffer is not null)
                {
                    var texturePointer = dxgiBuffer.GetResource(typeof(ID3D11Texture2D).GUID);
                    using var texture = new ID3D11Texture2D(texturePointer);
                    onFrame(new DecodedFrame(texture, (int)dxgiBuffer.SubresourceIndex, _width, _height, timestamp));
                    return;
                }
            }
            finally
            {
                dxgiBuffer?.Dispose();
            }
        }

        // Software path: NV12 in system memory.
        using var buffer2d = buffer.QueryInterfaceOrNull<IMF2DBuffer>();
        if (buffer2d is not null)
        {
            buffer2d.Lock2D(out var scanline0, out var pitch);
            try
            {
                onFrame(new DecodedFrame(scanline0, pitch, _surfaceHeight, _width, _height, timestamp));
            }
            finally
            {
                buffer2d.Unlock2D();
            }
        }
        else
        {
            buffer.Lock(out var data, out _, out _);
            try
            {
                onFrame(new DecodedFrame(data, _cpuPitch, _surfaceHeight, _width, _height, timestamp));
            }
            finally
            {
                buffer.Unlock();
            }
        }
    }

    public void Dispose()
    {
        try
        {
            _transform.ProcessMessage(TMessageType.MessageNotifyEndOfStream, UIntPtr.Zero);
            _transform.ProcessMessage(TMessageType.MessageNotifyEndStreaming, UIntPtr.Zero);
        }
        catch (SharpGenException)
        {
        }
        _transform.Dispose();
    }
}
