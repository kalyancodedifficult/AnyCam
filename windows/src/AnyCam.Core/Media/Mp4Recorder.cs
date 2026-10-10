using System.Collections.Concurrent;
using AnyCam.Core.Protocol;
using Vortice.MediaFoundation;

namespace AnyCam.Core.Media;

/// <summary>
/// Records the phone's own H.264/HEVC bitstream (no re-encoding, so no quality loss and almost no CPU)
/// plus the microphone audio (encoded to AAC) into an MP4 file, using the Media Foundation sink writer.
/// Writing happens on a dedicated thread; producers just enqueue copies of the packets.
/// </summary>
public sealed class Mp4Recorder : IDisposable
{
    private readonly record struct Item(bool IsVideo, byte[] Data, long TimestampUs, bool Keyframe);

    private readonly BlockingCollection<Item> _queue = new(boundedCapacity: 2048);
    private readonly Thread _thread;
    private readonly VideoStreamInfo _video;
    private readonly AudioStreamInfo? _audio;
    private IMFSinkWriter? _writer;
    private int _videoStream = -1;
    private int _audioStream = -1;
    private long _baseUs = -1;
    private long _lastVideoUs;
    private long _lastVideoTime = -1;
    private long _bytes;

    public Mp4Recorder(string path, VideoStreamInfo video, AudioStreamInfo? audio)
    {
        Path = path;
        _video = video;
        _audio = audio;
        MediaPlatform.EnsureStarted();
        _thread = new Thread(WriterLoop) { IsBackground = true, Name = "AnyCam recorder" };
        _thread.Start();
    }

    public string Path { get; }

    /// <summary>Recorded duration so far.</summary>
    public TimeSpan Duration => _baseUs < 0 ? TimeSpan.Zero : TimeSpan.FromTicks((_lastVideoUs - _baseUs) * 10);

    public long BytesWritten => Interlocked.Read(ref _bytes);

    /// <summary>Set if the sink writer failed; recording stops.</summary>
    public string? Error { get; private set; }

    public bool IsStarted => _baseUs >= 0;

    public void WriteVideo(ReadOnlySpan<byte> accessUnit, long timestampUs, bool keyframe)
    {
        if (Error is null && !_queue.IsAddingCompleted)
            _queue.TryAdd(new Item(true, accessUnit.ToArray(), timestampUs, keyframe));
    }

    public void WriteAudio(ReadOnlySpan<byte> pcm, long timestampUs)
    {
        if (Error is null && _audio is not null && !_queue.IsAddingCompleted)
            _queue.TryAdd(new Item(false, pcm.ToArray(), timestampUs, false));
    }

    private void WriterLoop()
    {
        try
        {
            foreach (var item in _queue.GetConsumingEnumerable())
            {
                if (Error is not null)
                    continue;
                try
                {
                    if (item.IsVideo)
                        WriteVideoSample(item);
                    else
                        WriteAudioSample(item);
                }
                catch (Exception ex)
                {
                    Error = ex.Message;
                }
            }
        }
        finally
        {
            try
            {
                if (_writer is not null && _baseUs >= 0)
                    _writer.Finalize();
            }
            catch (Exception ex)
            {
                Error ??= $"Finishing the recording failed: {ex.Message}";
            }
            _writer?.Dispose();
            _writer = null;
        }
    }

    private void WriteVideoSample(Item item)
    {
        if (_writer is null)
        {
            // The file must start at a keyframe that carries the parameter sets.
            var keyframe = item.Keyframe || AnnexB.ContainsKeyframe(item.Data, _video.IsHevc);
            if (!keyframe)
                return;
            var parameterSets = AnnexB.ExtractParameterSets(item.Data, _video.IsHevc);
            if (parameterSets.Length == 0)
                return;
            Open(parameterSets);
            _baseUs = item.TimestampUs;
        }

        var duration = (long)(10_000_000 / Math.Max(_video.Fps, 1));
        // MP4 needs strictly increasing timestamps; keep at least 1 ms between frames.
        var time = (item.TimestampUs - _baseUs) * 10;
        if (_lastVideoTime >= 0 && time < _lastVideoTime + 10_000)
            time = _lastVideoTime + 10_000;
        _lastVideoTime = time;
        using var sample = CreateSample(item.Data, time, duration);
        if (item.Keyframe || AnnexB.ContainsKeyframe(item.Data, _video.IsHevc))
            sample.Set(SampleAttributeKeys.CleanPoint, 1u);
        _writer!.WriteSample(_videoStream, sample);
        _lastVideoUs = item.TimestampUs;
        Interlocked.Add(ref _bytes, item.Data.Length);
    }

    private void WriteAudioSample(Item item)
    {
        if (_writer is null || _audioStream < 0 || item.TimestampUs < _baseUs)
            return; // audio before the first video keyframe is dropped so both start together

        var bytesPerFrame = 2 * _audio!.Channels;
        var frames = item.Data.Length / bytesPerFrame;
        var duration = frames * 10_000_000L / _audio.SampleRate;
        using var sample = CreateSample(item.Data, (item.TimestampUs - _baseUs) * 10, duration);
        _writer.WriteSample(_audioStream, sample);
    }

    private static IMFSample CreateSample(byte[] data, long time, long duration)
    {
        var sample = MediaFactory.MFCreateSample();
        using var buffer = MediaFactory.MFCreateMemoryBuffer(data.Length);
        buffer.Lock(out var pointer, out _, out _);
        System.Runtime.InteropServices.Marshal.Copy(data, 0, pointer, data.Length);
        buffer.Unlock();
        buffer.CurrentLength = data.Length;
        sample.AddBuffer(buffer);
        sample.SampleTime = time;
        sample.SampleDuration = duration;
        return sample;
    }

    private void Open(byte[] parameterSets)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        using var attributes = MediaFactory.MFCreateAttributes(2);
        attributes.Set(SinkWriterAttributeKeys.DisableThrottling, 1u);
        _writer = MediaFactory.MFCreateSinkWriterFromURL(Path, null!, attributes);

        using (var videoType = MediaFactory.MFCreateMediaType())
        {
            var (num, den) = MfConstants.FpsRatio(_video.Fps);
            videoType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            videoType.Set(MediaTypeAttributeKeys.Subtype, _video.IsHevc ? VideoFormatGuids.Hevc : VideoFormatGuids.H264);
            videoType.Set(MediaTypeAttributeKeys.FrameSize, MfConstants.PackSize(_video.Width, _video.Height));
            videoType.Set(MediaTypeAttributeKeys.FrameRate, MfConstants.PackRatio(num, den));
            videoType.Set(MediaTypeAttributeKeys.PixelAspectRatio, MfConstants.PackRatio(1, 1));
            videoType.Set(MediaTypeAttributeKeys.InterlaceMode, (uint)VideoInterlaceMode.Progressive);
            if (_video.Bitrate > 0)
                videoType.Set(MediaTypeAttributeKeys.AvgBitrate, (uint)Math.Min(_video.Bitrate, uint.MaxValue));
            videoType.SetBlob(MfConstants.MpegSequenceHeader, parameterSets);
            _videoStream = _writer.AddStream(videoType);
            _writer.SetInputMediaType(_videoStream, videoType, null!);
        }

        if (_audio is not null)
        {
            try
            {
                using var output = MediaFactory.MFCreateMediaType();
                output.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
                output.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Aac);
                output.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, (uint)_audio.SampleRate);
                output.Set(MediaTypeAttributeKeys.AudioNumChannels, (uint)_audio.Channels);
                output.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16u);
                output.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, 24000u); // 192 kb/s
                output.Set(MediaTypeAttributeKeys.AacPayloadType, 0u);
                _audioStream = _writer.AddStream(output);

                using var input = MediaFactory.MFCreateMediaType();
                input.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
                input.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Pcm);
                input.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, (uint)_audio.SampleRate);
                input.Set(MediaTypeAttributeKeys.AudioNumChannels, (uint)_audio.Channels);
                input.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16u);
                input.Set(MediaTypeAttributeKeys.AudioBlockAlignment, (uint)(2 * _audio.Channels));
                input.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, (uint)(2 * _audio.Channels * _audio.SampleRate));
                input.Set(MediaTypeAttributeKeys.AllSamplesIndependent, 1u);
                _writer.SetInputMediaType(_audioStream, input, null!);
            }
            catch
            {
                _audioStream = -1; // keep recording video even if AAC isn't available
            }
        }

        _writer.BeginWriting();
    }

    /// <summary>Stops accepting packets and finalizes the file (blocks until written).</summary>
    public void Dispose()
    {
        _queue.CompleteAdding();
        _thread.Join(TimeSpan.FromSeconds(15));
        _queue.Dispose();
    }
}
