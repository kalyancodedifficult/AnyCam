using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using AnyCam.Core.Protocol;
using AnyCam.Core.Transport;
using AnyCam.Core.VirtualCamera;

namespace AnyCam.Core.Media;

/// <summary>Snapshot of the engine's activity for the status bar.</summary>
public sealed record EngineStats
{
    public double DecodeFps { get; init; }
    public double OutputFps { get; init; }
    public string Decoder { get; init; } = "";
    public bool HardwareDecoding { get; init; }
    public int VideoWidth { get; init; }
    public int VideoHeight { get; init; }
    public int ProgramWidth { get; init; }
    public int ProgramHeight { get; init; }
    public VCamRequest? VirtualCamera { get; init; }
    public long DroppedFrames { get; init; }
    public double ProcessMs { get; init; }
    public string Gpu { get; init; } = "";
}

/// <summary>
/// Owns the GPU and runs the video pipeline on one dedicated thread:
/// compressed packets → decoder → <see cref="FrameProcessor"/> → preview + virtual camera.
/// When no video is flowing it keeps feeding the virtual camera with a slate image.
/// </summary>
public sealed class MediaEngine : IDisposable
{
    private const int MaxQueuedPackets = 12; // ~0.2–0.4 s; beyond that we skip to the next keyframe

    private readonly Thread _thread;
    private readonly AutoResetEvent _wake = new(false);
    private readonly ConcurrentQueue<Action> _commands = new();
    private readonly ConcurrentQueue<MediaPacket> _packets = new();
    private volatile bool _stopping;
    private volatile ProcessingSettings _settings = new();
    private volatile EngineStats _stats = new();

    // Engine-thread state.
    private GpuDevice? _gpu;
    private FrameProcessor? _processor;
    private VideoDecoder? _decoder;
    private PreviewSurface? _preview;
    private VirtualCameraOutput? _vcam;
    private VideoStreamInfo? _video;
    private bool _waitForKeyframe = true;
    private bool _framePending;
    private long _lastFrameTicks;
    private long _lastVcamWrite;
    private long _lastPreview;
    private long _lastKeyframeRequest;
    private string? _lutPath;
    private VCamRequest? _vcamRequest;
    private long _dropped;
    private readonly FrameCounter _decodeCounter = new();
    private readonly FrameCounter _outputCounter = new();
    private double _processMs;

    // Read by the UI thread for click-to-focus.
    private volatile PreviewGeometry? _previewGeometry;

    private sealed record PreviewGeometry(int Width, int Height, Vector4 Map, Matrix3x2 ProgramToSource);

    public MediaEngine()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "AnyCam media engine", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    /// <summary>The decoder needs an IDR frame (start of stream, or after dropping late frames).</summary>
    public event Action? KeyframeNeeded;

    /// <summary>An app started or stopped using the virtual camera (null = nobody).</summary>
    public event Action<VCamRequest?>? VirtualCameraConsumerChanged;

    /// <summary>Errors worth showing to the user.</summary>
    public event Action<string>? Error;

    public EngineStats Stats => _stats;

    public ProcessingSettings Settings
    {
        get => _settings;
        set
        {
            _settings = value;
            _wake.Set();
        }
    }

    public void SubmitVideo(MediaPacket packet)
    {
        if (_packets.Count >= MaxQueuedPackets)
        {
            // The pipeline fell behind (very slow GPU or a burst after a Wi‑Fi stall): drop everything
            // queued and resume at the next keyframe, which keeps latency bounded.
            while (_packets.TryDequeue(out var stale))
            {
                stale.Release();
                Interlocked.Increment(ref _dropped);
            }
            Post(() => _waitForKeyframe = true);
        }
        _packets.Enqueue(packet);
        _wake.Set();
    }

    /// <summary>The camera announced a (new) bitstream format.</summary>
    public void SetVideoStream(VideoStreamInfo info) => Post(() =>
    {
        _video = info;
        DisposeDecoder();
        _waitForKeyframe = true;
    });

    /// <summary>The camera went away: show the slate.</summary>
    public void ClearVideo() => Post(() =>
    {
        while (_packets.TryDequeue(out var packet))
            packet.Release();
        _video = null;
        DisposeDecoder();
        _processor?.ClearInput();
        _framePending = true;
    });

    public void SetSlate(byte[] bgra, int width, int height) => Post(() =>
    {
        _processor?.SetSlate(bgra, width, height);
        _framePending = true;
    });

    public void AttachPreview(IntPtr hwnd, int width, int height) => Post(() =>
    {
        _preview?.Dispose();
        _preview = _gpu is null ? null : new PreviewSurface(_gpu, hwnd, width, height);
        _framePending = true;
    });

    public void ResizePreview(int width, int height) => Post(() =>
    {
        _preview?.Resize(width, height);
        _framePending = true;
    });

    public void DetachPreview() => Post(() =>
    {
        _preview?.Dispose();
        _preview = null;
        _previewGeometry = null;
    });

    /// <summary>Reads back the processed program image (BGRA, top-down).</summary>
    public Task<(byte[] Pixels, int Width, int Height)?> CaptureProgramAsync()
    {
        var completion = new TaskCompletionSource<(byte[], int, int)?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() =>
        {
            try
            {
                completion.SetResult(_processor?.ReadProgram());
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        });
        return completion.Task;
    }

    /// <summary>
    /// Converts a point in the preview window (pixels) to normalized coordinates in the camera's
    /// encoded image, for tap-to-focus. Returns false outside the picture.
    /// </summary>
    public bool TryMapPreviewPoint(double x, double y, out Vector2 cameraPoint)
    {
        cameraPoint = default;
        var geometry = _previewGeometry;
        if (geometry is null || geometry.Width <= 0 || geometry.Height <= 0)
            return false;
        var u = x / geometry.Width;
        var v = y / geometry.Height;
        var programU = u * geometry.Map.X + geometry.Map.Z;
        var programV = v * geometry.Map.Y + geometry.Map.W;
        if (programU is < 0 or > 1 || programV is < 0 or > 1)
            return false;
        cameraPoint = Vector2.Transform(new Vector2((float)programU, (float)programV), geometry.ProgramToSource);
        return cameraPoint.X is >= 0 and <= 1 && cameraPoint.Y is >= 0 and <= 1;
    }

    private void Post(Action command)
    {
        _commands.Enqueue(command);
        _wake.Set();
    }

    // ---------------------------------------------------------------------------------------

    private void Run()
    {
        try
        {
            _gpu = GpuDevice.Create();
            _processor = new FrameProcessor(_gpu);
            _vcam = new VirtualCameraOutput();
            _stats = _stats with { Gpu = _gpu.AdapterName };
        }
        catch (Exception ex)
        {
            Error?.Invoke($"Graphics initialization failed: {ex.Message}");
            return;
        }

        while (!_stopping)
        {
            // Idle: tick at ~30 fps so the virtual camera and preview keep showing the slate.
            _wake.WaitOne(33);
            if (_stopping)
                break;

            while (_commands.TryDequeue(out var command))
                RunSafely(command);

            var started = Stopwatch.GetTimestamp();
            var decoded = DecodePending();
            PollVirtualCamera();

            var settings = _settings;
            if (settings.LutPath != _lutPath)
                LoadLut(settings.LutPath);

            var now = Environment.TickCount64;
            var idle = now - _lastFrameTicks > 500;
            if (decoded || _framePending || idle)
            {
                RunSafely(() => Render(settings, decoded, idle, now));
                _framePending = false;
                if (decoded)
                    _processMs = _processMs * 0.9 + Stopwatch.GetElapsedTime(started).TotalMilliseconds * 0.1;
            }
            PublishStats();
        }

        DisposeDecoder();
        _preview?.Dispose();
        _vcam?.Dispose();
        _processor?.Dispose();
        _gpu?.Dispose();
    }

    private bool DecodePending()
    {
        var decoded = false;
        while (_packets.TryDequeue(out var packet))
        {
            try
            {
                if (_video is null)
                    continue;

                if (_waitForKeyframe)
                {
                    if (!packet.IsKeyframe && !AnnexB.ContainsKeyframe(packet.Span, _video.IsHevc))
                    {
                        RequestKeyframe();
                        continue;
                    }
                    _waitForKeyframe = false;
                }

                if (_decoder is null)
                {
                    _decoder = new VideoDecoder(_gpu!, _video.IsHevc, _video.Width, _video.Height, _video.Fps);
                    _stats = _stats with { Decoder = _decoder.Name, HardwareDecoding = _decoder.IsHardware };
                }

                _decoder.Decode(packet.Span, packet.Timestamp * 10, (in DecodedFrame frame) =>
                {
                    _processor!.Upload(frame);
                    decoded = true;
                    _decodeCounter.Tick();
                });
            }
            catch (NotSupportedException ex)
            {
                Error?.Invoke(ex.Message);
                DisposeDecoder();
                _video = null; // don't retry every frame
            }
            catch (Exception ex)
            {
                Error?.Invoke($"Video decoding failed: {ex.Message}");
                DisposeDecoder();
                _waitForKeyframe = true;
                RequestKeyframe();
            }
            finally
            {
                packet.Release();
            }
        }

        if (decoded)
            _lastFrameTicks = Environment.TickCount64;
        return decoded;
    }

    private void RequestKeyframe()
    {
        var now = Environment.TickCount64;
        if (now - _lastKeyframeRequest < 500)
            return;
        _lastKeyframeRequest = now;
        KeyframeNeeded?.Invoke();
    }

    private void PollVirtualCamera()
    {
        var request = _vcam!.Poll();
        if (request != _vcamRequest)
        {
            _vcamRequest = request;
            _framePending = true;
            VirtualCameraConsumerChanged?.Invoke(request);
        }
    }

    private void Render(ProcessingSettings settings, bool decoded, bool idle, long now)
    {
        var processor = _processor!;
        var color = _video is null ? VideoColor.Bt709Video : new VideoColor(_video.IsFullRange, false);

        var wantVcam = _vcamRequest is { } request && (decoded || now - _lastVcamWrite >= 1000 / Math.Max(request.Fps, 1) * 0.75);
        var wantPreview = _preview is not null && (decoded || _framePending || now - _lastPreview >= (idle ? 66 : 33));
        if (!wantVcam && !wantPreview)
            return;

        processor.Develop(settings, color);
        if (!processor.HasProgram)
            return;

        if (wantVcam && _vcamRequest is { } vcamRequest)
        {
            var timestamp = Stopwatch.GetTimestamp() * 10_000_000 / Stopwatch.Frequency;
            if (processor.WriteVirtualCamera(_vcam!, vcamRequest, settings.Fill, timestamp))
            {
                _lastVcamWrite = now;
                _outputCounter.Tick();
            }
        }

        if (wantPreview && _preview is not null)
        {
            processor.RenderPreview(_preview, settings);
            _lastPreview = now;
            _previewGeometry = new PreviewGeometry(_preview.Width, _preview.Height,
                processor.PreviewMap(_preview.Width, _preview.Height), ProgramToSourceMatrix(processor));
        }
    }

    private static Matrix3x2 ProgramToSourceMatrix(FrameProcessor processor)
    {
        var origin = processor.ProgramToSource(Vector2.Zero);
        var du = processor.ProgramToSource(Vector2.UnitX) - origin;
        var dv = processor.ProgramToSource(Vector2.UnitY) - origin;
        return new Matrix3x2(du.X, du.Y, dv.X, dv.Y, origin.X, origin.Y);
    }

    private void LoadLut(string? path)
    {
        _lutPath = path;
        try
        {
            _processor!.SetLut(string.IsNullOrEmpty(path) ? null : CubeLut.Load(path));
        }
        catch (Exception ex)
        {
            _processor!.SetLut(null);
            Error?.Invoke($"Couldn't load LUT: {ex.Message}");
        }
        _framePending = true;
    }

    private void PublishStats()
    {
        _stats = _stats with
        {
            DecodeFps = _decodeCounter.Rate,
            OutputFps = _outputCounter.Rate,
            VideoWidth = _decoder?.Width ?? 0,
            VideoHeight = _decoder?.Height ?? 0,
            ProgramWidth = _processor?.ProgramWidth ?? 0,
            ProgramHeight = _processor?.ProgramHeight ?? 0,
            VirtualCamera = _vcamRequest,
            DroppedFrames = Interlocked.Read(ref _dropped),
            ProcessMs = _processMs,
            Decoder = _decoder?.Name ?? "",
            HardwareDecoding = _decoder?.IsHardware ?? false,
        };
    }

    private void RunSafely(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Error?.Invoke(ex.Message);
        }
    }

    private void DisposeDecoder()
    {
        _decoder?.Dispose();
        _decoder = null;
    }

    public void Dispose()
    {
        _stopping = true;
        _wake.Set();
        _thread.Join(TimeSpan.FromSeconds(3));
        while (_packets.TryDequeue(out var packet))
            packet.Release();
        _wake.Dispose();
    }
}

/// <summary>Measures an event rate over a sliding one-second window.</summary>
internal sealed class FrameCounter
{
    private readonly Queue<long> _ticks = new();

    public void Tick()
    {
        var now = Environment.TickCount64;
        lock (_ticks)
        {
            _ticks.Enqueue(now);
            while (_ticks.Count > 0 && now - _ticks.Peek() > 1000)
                _ticks.Dequeue();
        }
    }

    public double Rate
    {
        get
        {
            var now = Environment.TickCount64;
            lock (_ticks)
            {
                while (_ticks.Count > 0 && now - _ticks.Peek() > 1000)
                    _ticks.Dequeue();
                return _ticks.Count;
            }
        }
    }
}
