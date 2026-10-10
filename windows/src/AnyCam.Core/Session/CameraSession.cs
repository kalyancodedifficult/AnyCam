using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using AnyCam.Core.Media;
using AnyCam.Core.Protocol;
using AnyCam.Core.Transport;
using AnyCam.Core.VirtualCamera;

namespace AnyCam.Core.Session;

public enum SessionStatus
{
    Idle,
    Connecting,
    Connected,
    Reconnecting,
}

/// <summary>Where the phone's microphone goes.</summary>
public sealed record AudioOutputSettings
{
    public bool Enabled { get; init; }
    public string? DeviceId { get; init; }
    public int DelayMs { get; init; }
    public float Volume { get; init; } = 1;
}

/// <summary>
/// One phone connection with automatic reconnects. Routes media to the engine, recorder and audio
/// output, keeps the latest caps/state, and sends camera commands. Events fire on background threads.
/// </summary>
public sealed class CameraSession : IAsyncDisposable
{
    private static readonly string AppVersion =
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.1.0";

    private readonly MediaEngine _engine;
    private readonly AudioPlayback _audio = new();
    private readonly object _lock = new();
    private JsonObject _stateJson = new();
    private CameraConnection? _connection;
    private CancellationTokenSource? _runCts;
    private Task? _runTask;
    private Mp4Recorder? _recorder;
    private AudioOutputSettings _audioSettings = new();
    private bool _virtualCameraLive;
    private int _pingId;
    private int _photoId;
    private long _lastBytes;
    private long _lastBytesTicks;

    public CameraSession(MediaEngine engine)
    {
        _engine = engine;
        _engine.KeyframeNeeded += () => _connection?.Send(HostMessages.Keyframe());
        _engine.VirtualCameraConsumerChanged += request =>
        {
            _virtualCameraLive = request is not null;
            SendTally();
            RaiseChanged();
        };
    }

    public SessionStatus Status { get; private set; }
    public string? StatusMessage { get; private set; }
    public CameraEndpoint? Endpoint { get; private set; }
    public TransportKind? Transport => _connection?.Kind;
    public CameraHello? Hello { get; private set; }
    public CameraCaps? Caps { get; private set; }
    public CameraState State { get; private set; } = new();
    public VideoStreamInfo? Video { get; private set; }
    public AudioStreamInfo? AudioFormat { get; private set; }
    public double RoundTripMs { get; private set; }
    public double ReceiveMbps { get; private set; }
    public bool VirtualCameraLive => _virtualCameraLive;

    /// <summary>Configuration requested on every (re)connect; null lets the phone use its own defaults.</summary>
    public StreamConfig? PreferredConfig { get; set; }

    /// <summary>Folder for photos taken with <see cref="CapturePhoto"/>.</summary>
    public string PhotoFolder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "AnyCam");

    public bool IsRecording
    {
        get
        {
            lock (_lock)
                return _recorder is not null;
        }
    }

    public TimeSpan RecordingDuration
    {
        get
        {
            lock (_lock)
                return _recorder?.Duration ?? TimeSpan.Zero;
        }
    }

    public string? RecordingPath
    {
        get
        {
            lock (_lock)
                return _recorder?.Path;
        }
    }

    /// <summary>Something changed (status, caps, state, stats). Raised on a background thread.</summary>
    public event Action? Changed;

    /// <summary>A message for the user (photo saved, phone warning, recording error...).</summary>
    public event Action<string>? Notification;

    private void RaiseChanged() => Changed?.Invoke();
    private void Notify(string message) => Notification?.Invoke(message);

    // ---------------------------------------------------------------------------------------
    // Connection lifecycle

    public void Connect(CameraEndpoint endpoint)
    {
        _ = DisconnectAsync().ContinueWith(_ =>
        {
            Endpoint = endpoint;
            _runCts = new CancellationTokenSource();
            _runTask = Task.Run(() => RunAsync(endpoint, _runCts.Token));
        }, TaskScheduler.Default);
    }

    public async Task DisconnectAsync()
    {
        var cts = _runCts;
        var task = _runTask;
        _runCts = null;
        _runTask = null;
        if (cts is null)
            return;
        cts.Cancel();
        if (_connection is { } connection)
            await connection.DisposeAsync().ConfigureAwait(false);
        if (task is not null)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch
            {
                // reported through status
            }
        }
        cts.Dispose();
        Endpoint = null;
        SetStatus(SessionStatus.Idle, null);
    }

    private async Task RunAsync(CameraEndpoint endpoint, CancellationToken token)
    {
        var attempt = 0;
        while (!token.IsCancellationRequested)
        {
            SetStatus(attempt == 0 ? SessionStatus.Connecting : SessionStatus.Reconnecting, StatusMessage);
            CameraConnection? connection = null;
            try
            {
                var stream = await endpoint.OpenAsync(token).ConfigureAwait(false);
                connection = new CameraConnection(stream, endpoint.Kind, endpoint.DisplayName);
                var closed = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
                connection.ControlReceived += OnControl;
                connection.MediaReceived += OnMedia;
                connection.Closed += error => closed.TrySetResult(error);
                _connection = connection;
                connection.Start();

                await connection.SendControlAsync(HostMessages.Hello(Environment.MachineName, AppVersion), token).ConfigureAwait(false);
                if (PreferredConfig is { } config)
                    await connection.SendControlAsync(HostMessages.Configure(config), token).ConfigureAwait(false);
                await connection.SendControlAsync(HostMessages.Start(), token).ConfigureAwait(false);
                SendTally();

                attempt = 0;
                _lastBytes = 0;
                _lastBytesTicks = Environment.TickCount64;
                SetStatus(SessionStatus.Connected, null);

                while (!token.IsCancellationRequested)
                {
                    var finished = await Task.WhenAny(closed.Task, Task.Delay(1000, token)).ConfigureAwait(false);
                    if (finished == closed.Task)
                    {
                        var error = closed.Task.Result;
                        StatusMessage = error is null ? "Disconnected" : $"Connection lost: {error.Message}";
                        break;
                    }
                    if (DateTime.UtcNow - connection.LastReceiveUtc > TimeSpan.FromSeconds(5))
                    {
                        StatusMessage = "The phone stopped responding";
                        break;
                    }
                    connection.Send(HostMessages.Ping(++_pingId, NowMicroseconds()));
                    UpdateThroughput(connection);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                StatusMessage = ex is UsbmuxException or System.Net.Sockets.SocketException or TimeoutException or OperationCanceledException
                    ? ex is OperationCanceledException ? "Connection timed out" : ex.Message
                    : $"Connection failed: {ex.Message}";
            }
            finally
            {
                if (connection is not null)
                {
                    _connection = null;
                    await connection.DisposeAsync().ConfigureAwait(false);
                    OnDisconnected();
                }
            }

            if (token.IsCancellationRequested)
                break;
            attempt++;
            SetStatus(SessionStatus.Reconnecting, StatusMessage);
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(1 + attempt, 5)), token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void OnDisconnected()
    {
        _engine.ClearVideo();
        _audio.Stop();
        StopRecording();
        Video = null;
        AudioFormat = null;
        lock (_lock)
            _stateJson = new JsonObject();
        State = new CameraState();
        RaiseChanged();
    }

    private void SetStatus(SessionStatus status, string? message)
    {
        Status = status;
        StatusMessage = message;
        RaiseChanged();
    }

    private void UpdateThroughput(CameraConnection connection)
    {
        var now = Environment.TickCount64;
        var bytes = connection.BytesReceived;
        if (_lastBytesTicks > 0 && now > _lastBytesTicks)
            ReceiveMbps = (bytes - _lastBytes) * 8.0 / ((now - _lastBytesTicks) / 1000.0) / 1_000_000;
        _lastBytes = bytes;
        _lastBytesTicks = now;
        RaiseChanged();
    }

    private static long NowMicroseconds() => Stopwatch.GetTimestamp() * 1_000_000 / Stopwatch.Frequency;

    // ---------------------------------------------------------------------------------------
    // Incoming

    private void OnControl(JsonObject message)
    {
        try
        {
            switch (message.MessageType())
            {
                case "hello":
                    Hello = message.As<CameraHello>();
                    if (Hello is { Proto: > 0 } hello && hello.Proto != ProtocolConstants.Version)
                        Notify($"The phone app speaks protocol v{hello.Proto}, this PC app v{ProtocolConstants.Version}. Update both to the same release.");
                    break;
                case "caps":
                    Caps = message.As<CameraCaps>();
                    break;
                case "state":
                    lock (_lock)
                    {
                        foreach (var (key, value) in message)
                        {
                            if (key != "t")
                                _stateJson[key] = value?.DeepClone();
                        }
                        State = _stateJson.As<CameraState>() ?? new CameraState();
                    }
                    break;
                case "video":
                    var video = message.As<VideoStreamInfo>();
                    if (video is not null)
                    {
                        var formatChanged = Video is not null && (Video.Codec != video.Codec || Video.Width != video.Width || Video.Height != video.Height);
                        Video = video;
                        _engine.SetVideoStream(video);
                        if (formatChanged && IsRecording)
                        {
                            StopRecording();
                            Notify("Recording stopped because the stream format changed.");
                        }
                    }
                    break;
                case "audio":
                    AudioFormat = message.As<AudioStreamInfo>();
                    RestartAudio();
                    break;
                case "pong":
                    if (message["ts"] is JsonValue ts && ts.TryGetValue(out long sent))
                        RoundTripMs = (NowMicroseconds() - sent) / 1000.0;
                    break;
                case "event":
                    if (message.As<CameraEvent>() is { Message: { Length: > 0 } text } cameraEvent && cameraEvent.Level != "info")
                        Notify(text);
                    break;
            }
        }
        catch (JsonException)
        {
            // Ignore malformed messages.
        }
        RaiseChanged();
    }

    private void OnMedia(MediaPacket packet)
    {
        switch (packet.Type)
        {
            case PacketType.Video:
                lock (_lock)
                    _recorder?.WriteVideo(packet.Span, packet.Timestamp, packet.IsKeyframe);
                _engine.SubmitVideo(packet); // engine releases the packet
                return;

            case PacketType.Audio:
                try
                {
                    _audio.Write(packet.Span);
                    lock (_lock)
                        _recorder?.WriteAudio(packet.Span, packet.Timestamp);
                }
                finally
                {
                    packet.Release();
                }
                return;

            case PacketType.Photo:
                try
                {
                    SavePhoto(packet.Span);
                }
                catch (Exception ex)
                {
                    Notify($"Couldn't save the photo: {ex.Message}");
                }
                finally
                {
                    packet.Release();
                }
                return;

            default:
                packet.Release();
                return;
        }
    }

    private void SavePhoto(ReadOnlySpan<byte> payload)
    {
        var jsonLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(payload);
        var info = JsonSerializer.Deserialize<PhotoInfo>(payload.Slice(4, jsonLength), ProtocolJson.Options) ?? new PhotoInfo();
        var image = payload[(4 + jsonLength)..];
        Directory.CreateDirectory(PhotoFolder);
        var extension = info.Format == "heic" ? "heic" : "jpg";
        var path = Path.Combine(PhotoFolder, $"AnyCam_{DateTime.Now:yyyyMMdd_HHmmss}.{extension}");
        File.WriteAllBytes(path, image.ToArray());
        Notify($"Photo saved: {path}");
    }

    // ---------------------------------------------------------------------------------------
    // Commands

    /// <summary>Changes camera settings, e.g. Set(("zoom", 2.0)). Applied optimistically to <see cref="State"/>.</summary>
    public void Set(params (string Key, JsonNode? Value)[] values)
    {
        var connection = _connection;
        if (connection is null)
            return;
        lock (_lock)
        {
            foreach (var (key, value) in values)
                _stateJson[key] = value?.DeepClone();
            State = _stateJson.As<CameraState>() ?? new CameraState();
        }
        connection.Send(HostMessages.Set(values));
        RaiseChanged();
    }

    /// <summary>Changes the stream (camera, size, fps, codec, bitrate…). The phone restarts its encoder.</summary>
    public void Configure(StreamConfig config)
    {
        PreferredConfig = PreferredConfig is null ? config : Merge(PreferredConfig, config);
        _connection?.Send(HostMessages.Configure(config));
    }

    private static StreamConfig Merge(StreamConfig current, StreamConfig update) => current with
    {
        Camera = update.Camera ?? current.Camera,
        Width = update.Width > 0 ? update.Width : current.Width,
        Height = update.Height > 0 ? update.Height : current.Height,
        Fps = update.Fps > 0 ? update.Fps : current.Fps,
        Codec = update.Codec ?? current.Codec,
        Bitrate = update.Bitrate > 0 ? update.Bitrate : current.Bitrate,
        Orientation = update.Orientation ?? current.Orientation,
        Audio = update.Audio ?? current.Audio,
        KeyframeInterval = update.KeyframeInterval > 0 ? update.KeyframeInterval : current.KeyframeInterval,
    };

    /// <summary>Tap-to-focus/expose at a point of the camera image (0..1).</summary>
    public void FocusAt(Vector2 point) => _connection?.Send(HostMessages.Focus(point.X, point.Y));

    public void CapturePhoto()
    {
        if (_connection is null)
            return;
        _connection.Send(HostMessages.Photo(Interlocked.Increment(ref _photoId)));
    }

    public void SetPhoneDimmed(bool dimmed) => _connection?.Send(HostMessages.Dim(dimmed));

    public void RequestKeyframe() => _connection?.Send(HostMessages.Keyframe());

    private void SendTally() => _connection?.Send(HostMessages.Tally(_virtualCameraLive, IsRecording));

    // ---------------------------------------------------------------------------------------
    // Recording

    /// <summary>Starts an MP4 recording of the original stream. Returns the file path or null.</summary>
    public string? StartRecording(string folder)
    {
        var video = Video;
        if (video is null)
        {
            Notify("Connect a phone before recording.");
            return null;
        }

        string path;
        lock (_lock)
        {
            if (_recorder is not null)
                return _recorder.Path;
            path = Path.Combine(folder, $"AnyCam_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");
            _recorder = new Mp4Recorder(path, video, AudioFormat);
        }
        RequestKeyframe(); // the file starts at the next keyframe
        SendTally();
        RaiseChanged();
        return path;
    }

    public void StopRecording()
    {
        Mp4Recorder? recorder;
        lock (_lock)
        {
            recorder = _recorder;
            _recorder = null;
        }
        if (recorder is null)
            return;

        SendTally();
        RaiseChanged();
        _ = Task.Run(() =>
        {
            var started = recorder.IsStarted;
            recorder.Dispose(); // finalizes the MP4
            if (recorder.Error is { } error)
                Notify($"Recording failed: {error}");
            else if (started)
                Notify($"Recording saved: {recorder.Path}");
        });
    }

    // ---------------------------------------------------------------------------------------
    // Audio

    public AudioOutputSettings AudioSettings
    {
        get => _audioSettings;
        set
        {
            var previous = _audioSettings;
            _audioSettings = value;
            if (_audio.IsPlaying && previous with { Volume = value.Volume } == value)
            {
                _audio.Volume = value.Volume; // volume only: no restart (no glitch)
                return;
            }
            RestartAudio();
        }
    }

    private void RestartAudio()
    {
        _audio.Stop();
        var format = AudioFormat;
        if (!_audioSettings.Enabled || format is null)
            return;
        try
        {
            _audio.Volume = _audioSettings.Volume;
            _audio.Start(_audioSettings.DeviceId, format.SampleRate, format.Channels, _audioSettings.DelayMs);
        }
        catch (Exception ex)
        {
            Notify($"Couldn't start audio output: {ex.Message}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        StopRecording();
        _audio.Dispose();
    }
}
