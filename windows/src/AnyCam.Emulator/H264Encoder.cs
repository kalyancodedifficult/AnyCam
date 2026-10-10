using AnyCam.Core.Media;
using SharpGen.Runtime;
using Vortice.MediaFoundation;

namespace AnyCam.Emulator;

/// <summary>Software H.264 encoder (Microsoft "H264 Encoder MFT"), configured like the phone: no B-frames, low latency, fixed GOP.</summary>
internal sealed class H264Encoder : IDisposable
{
    private static readonly Guid ClsidH264Encoder = new("6CA50344-051A-4DED-9779-A43305165E35");
    private static readonly Guid Mpeg2Profile = new("ad76a80b-2d5c-4e0b-b375-64e520137036"); // MF_MT_MPEG2_PROFILE
    private const int NeedMoreInput = unchecked((int)0xC00D6D72);

    [System.Runtime.InteropServices.DllImport("ole32.dll")]
    private static extern int CoCreateInstance(in Guid clsid, IntPtr outer, uint context, in Guid iid, out IntPtr instance);

    [System.Runtime.InteropServices.DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr reserved, uint flags);

    private readonly IMFTransform _encoder;
    private readonly int _width;
    private readonly int _height;
    private readonly long _frameDuration;
    private readonly int _outputSize;

    public H264Encoder(int width, int height, int fps, int bitrate)
    {
        MediaPlatform.EnsureStarted();
        _width = width;
        _height = height;
        _frameDuration = 10_000_000 / fps;

        CoInitializeEx(IntPtr.Zero, 0); // COINIT_MULTITHREADED; harmless if already initialized
        var iid = typeof(IMFTransform).GUID;
        var hr = CoCreateInstance(ClsidH264Encoder, IntPtr.Zero, 1 /* CLSCTX_INPROC_SERVER */, iid, out var pointer);
        if (hr < 0)
            throw new NotSupportedException($"Can't create the H.264 encoder MFT (0x{hr:X8})");
        _encoder = new IMFTransform(pointer);

        using (var output = MediaFactory.MFCreateMediaType())
        {
            output.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            output.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
            output.Set(MediaTypeAttributeKeys.AvgBitrate, (uint)bitrate);
            output.Set(MediaTypeAttributeKeys.FrameSize, ((ulong)(uint)width << 32) | (uint)height);
            output.Set(MediaTypeAttributeKeys.FrameRate, ((ulong)(uint)fps << 32) | 1u);
            output.Set(MediaTypeAttributeKeys.PixelAspectRatio, (1ul << 32) | 1u);
            output.Set(MediaTypeAttributeKeys.InterlaceMode, (uint)VideoInterlaceMode.Progressive);
            output.Set(Mpeg2Profile, 100u); // High (CABAC), still no B-frames because of the settings below
            _encoder.SetOutputType(0, output, 0);
        }

        using (var input = MediaFactory.MFCreateMediaType())
        {
            input.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            input.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.NV12);
            input.Set(MediaTypeAttributeKeys.FrameSize, ((ulong)(uint)width << 32) | (uint)height);
            input.Set(MediaTypeAttributeKeys.FrameRate, ((ulong)(uint)fps << 32) | 1u);
            input.Set(MediaTypeAttributeKeys.PixelAspectRatio, (1ul << 32) | 1u);
            input.Set(MediaTypeAttributeKeys.InterlaceMode, (uint)VideoInterlaceMode.Progressive);
            _encoder.SetInputType(0, input, 0);
        }

        CodecApi.SetBool(_encoder.NativePointer, CodecApi.LowLatencyMode, true);
        CodecApi.SetUInt32(_encoder.NativePointer, CodecApi.BFrameCount, 0);
        CodecApi.SetUInt32(_encoder.NativePointer, CodecApi.GopSize, (uint)(fps * 2));
        CodecApi.SetUInt32(_encoder.NativePointer, CodecApi.RateControlMode, 0);
        CodecApi.SetUInt32(_encoder.NativePointer, CodecApi.MeanBitRate, (uint)bitrate);

        _outputSize = Math.Max(_encoder.GetOutputStreamInfo(0).Size, width * height * 2);
        _encoder.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
        _encoder.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);
    }

    public void ForceKeyframe() => CodecApi.SetUInt32(_encoder.NativePointer, CodecApi.ForceKeyFrame, 1);

    /// <summary>Encodes one NV12 frame; returns zero or more Annex-B access units with keyframe flags.</summary>
    public List<(byte[] Data, bool Keyframe, long Time)> Encode(byte[] nv12, long time100ns)
    {
        using (var sample = MediaFactory.MFCreateSample())
        using (var buffer = MediaFactory.MFCreateMemoryBuffer(nv12.Length))
        {
            buffer.Lock(out var pointer, out _, out _);
            System.Runtime.InteropServices.Marshal.Copy(nv12, 0, pointer, nv12.Length);
            buffer.Unlock();
            buffer.CurrentLength = nv12.Length;
            sample.AddBuffer(buffer);
            sample.SampleTime = time100ns;
            sample.SampleDuration = _frameDuration;
            _encoder.ProcessInput(0, sample, 0);
        }

        var results = new List<(byte[], bool, long)>();
        while (true)
        {
            using var outSample = MediaFactory.MFCreateSample();
            using var outBuffer = MediaFactory.MFCreateMemoryBuffer(_outputSize);
            outSample.AddBuffer(outBuffer);
            var output = new OutputDataBuffer { StreamID = 0, Sample = outSample };
            var result = _encoder.ProcessOutput(ProcessOutputFlags.None, 1, ref output, out _);
            output.Events?.Dispose();
            if (result.Code == NeedMoreInput)
                break;
            if (result.Failure)
                throw new SharpGenException(result);

            using var contiguous = outSample.ConvertToContiguousBuffer();
            contiguous.Lock(out var data, out _, out var length);
            var bytes = new byte[length];
            System.Runtime.InteropServices.Marshal.Copy(data, bytes, 0, length);
            contiguous.Unlock();

            var keyframe = outSample.GetUInt32(SampleAttributeKeys.CleanPoint, out var clean).Success && clean != 0;
            results.Add((bytes, keyframe, outSample.SampleTime));
        }
        return results;
    }

    public void Dispose()
    {
        try
        {
            _encoder.ProcessMessage(TMessageType.MessageNotifyEndOfStream, UIntPtr.Zero);
        }
        catch (SharpGenException)
        {
        }
        _encoder.Dispose();
    }
}
