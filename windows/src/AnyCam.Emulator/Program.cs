// AnyCam phone emulator: speaks the camera side of docs/PROTOCOL.md so AnyCam Desktop can be developed
// and tested without an iPhone. Streams an H.264 test pattern plus a 48 kHz tone track.
//
//   dotnet run --project AnyCam.Emulator -- [--port 47200] [--size 1280x720] [--fps 30] [--bitrate 6000000]

using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using AnyCam.Core.Protocol;
using AnyCam.Emulator;

var port = ProtocolConstants.DefaultPort;
var width = 1280;
var height = 720;
var fps = 30;
var bitrate = 6_000_000;
for (var i = 0; i < args.Length - 1; i++)
{
    switch (args[i])
    {
        case "--port": port = int.Parse(args[++i]); break;
        case "--size":
            var parts = args[++i].Split('x');
            width = int.Parse(parts[0]);
            height = int.Parse(parts[1]);
            break;
        case "--fps": fps = int.Parse(args[++i]); break;
        case "--bitrate": bitrate = int.Parse(args[++i]); break;
    }
}

NativeMethods.timeBeginPeriod(1);
var listener = new TcpListener(IPAddress.Any, port);
listener.Start();
Console.WriteLine($"AnyCam emulator listening on port {port} ({width}x{height} @ {fps} fps, {bitrate / 1e6:0.#} Mb/s). Connect AnyCam Desktop to 127.0.0.1.");

PhoneSession? current = null;
while (true)
{
    var client = await listener.AcceptTcpClientAsync();
    client.NoDelay = true;
    Console.WriteLine($"Host connected from {client.Client.RemoteEndPoint}");
    current?.Dispose();
    current = new PhoneSession(client, width, height, fps, bitrate);
    current.Start();
}

internal static class NativeMethods
{
    [DllImport("winmm.dll")]
    public static extern uint timeBeginPeriod(uint period);
}

/// <summary>One connected host.</summary>
internal sealed class PhoneSession : IDisposable
{
    // Small JPEG (64x36 test card) to exercise the photo path.
    private static readonly byte[] TinyJpeg = Convert.FromBase64String(
        "/9j/4AAQSkZJRgABAgAAAQABAAD//gAPTGF2YzYzLjEuMTAwAP/bAEMACBAQExATFhYWFhYWGhgaGxsbGhoaGhsbGx0dHSIiIh0dHRsbHR0gICIiJSYlIyMiIyYmKCgoMDAuLjg4OkVFU//EAKQAAAIDAQEBAAAAAAAAAAAAAAcACAUGBAMCAQADAQEBAQAAAAAAAAAAAAAABwYIAwUEEAABAwIEAgcCDwEAAAAAAAABAgADESESBAUxcUFRQhOygpHSwbPRBqGxknQUFVKBYTVzNDMyEQABAwIDBAUKAgsBAAAAAAABAgADEQQhEgUxgQbSIhNBUdMyI0KSkWGzsXEUNbKh0cHwg3I0M3RSghb/wAARCAAkAEADARIAAhIAAxIA/9oADAMBAAIRAxEAPwCP7WMYxnvTMriPbKFhZAIO/wCLotsN78HsIoxFGlA6oA2pXpNP1N3y0+3zHrVDAYJBHb/t3Ydnv+jqY0CNCUD0QBspX379r+vi/WDEn7CFRClgKnUlQwQa+aNKqBVgpXk9GgxCi0Re3Sr26muFVrLIpdCoqKQT0UZjSoSKJGAwGxj1rsGPazGM2urY0AxzFa8AMdSxjNrq2NAMdBpuawK7FRsr/m4ok9Hi+fld5YEggg0IuCOTZ2n3GVXVKOCvJ7ge7f8AP6ubBINRgR2t/cXaOJ4vvoUgSRDzwCTmkRgArCorGNpI8jaqiQG6FoTIlSVJCkqBSpKgCFA4EEHAgjaHLF1+Un+1ZaCfDh7aKOTDWuHGkKpWgrSu9A6tjQjHBBk/K6Llp6YlzDgU+1BY5KW/lj2BG8HmY7qfTIYtipN5TysyPuTAlMabqNBSpNSaczbdthphPEF2mickJoAKkLqfefObXkVt290a26ySQZ0Z1KVkTkCE1NcqRkwSNgHYGJ3ppMlGk2K/MfA9INJR8SXijjHb+qvxHo1omfie9iOEdvvTJ4rBrKydCyx6830keh6Yapt9duZqVRDuSvnL+ZwR1ScejH7FczN7wea1SeCuFMZ4hXsUHlZvyLhayk2yXO5UfhNzNcQaxcS7Uxbgrndex/8Aes+MjDF5K9TVzf3/AJWxy16y59aPwnDu9t9Lgl2qk3FPK+V9EaAoXaqbTk0O2SMFzesjkerX4cFyuUYhO6v63IvSf27JfVYPdJYzHxgzMSQlEWWSlICUpCFgACwAAkoABsHMPtcJENafp/cNONlDRrc+lL7U8jjY18WNasZs02RScwEjZYII4AkHjb5S/HT/AO1H4u4X52nrKZwkbFAg7hX2vlY/1CP+vyl1/F1tHNpa5VDpwKQpBwr01pQpJJBOUhVSARUpT3P0OKfwa6/g/Hjf21upjq2MZtdWxoBjmK14AY6ljGbXVsaAY6/SY04Fr6xVh/IAG3O9b8A/bSv8FfyHupba01AyLX2lWXcAD+3Hc+um/wBlX85+SW2+NrmQ3NvbVpGmLraCuK1KUmqhWhyhPRwqMyscX5/Gv4jD/io+LKzm1/Kx0DH/AP/Z");

    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly JsonObject _state;
    private int _width;
    private int _height;
    private int _fps;
    private int _bitrate;
    private volatile bool _streaming;
    private volatile bool _keyframeRequested;
    private volatile bool _restart;
    private long _queuedBytes;

    public PhoneSession(TcpClient client, int width, int height, int fps, int bitrate)
    {
        _client = client;
        _stream = client.GetStream();
        _width = width;
        _height = height;
        _fps = fps;
        _bitrate = bitrate;
        _state = new JsonObject
        {
            ["t"] = "state", ["streaming"] = false, ["camera"] = "emulator-back", ["width"] = width, ["height"] = height,
            ["fps"] = fps, ["codec"] = "h264", ["bitrate"] = bitrate, ["orientation"] = "landscape", ["audio"] = true,
            ["zoom"] = 1.0, ["focusMode"] = "continuous", ["lensPosition"] = 0.5, ["smoothFocus"] = true, ["faceDriven"] = true,
            ["exposureMode"] = "auto", ["iso"] = 100, ["shutter"] = 0.01, ["ev"] = 0.0, ["exposureOffset"] = 0.0,
            ["wbMode"] = "auto", ["temperature"] = 5200, ["tint"] = 0, ["torch"] = 0.0, ["stabilization"] = "off",
            ["hdr"] = false, ["lowLightBoost"] = false, ["battery"] = 0.87, ["charging"] = true, ["thermal"] = "nominal",
        };
    }

    public void Start()
    {
        _ = Task.Run(ReceiveLoopAsync);
        _ = Task.Run(StatusLoopAsync);
        _ = SendHandshakeAsync();
        var media = new Thread(MediaLoop) { IsBackground = true, Name = "emulator media" };
        media.Start();
    }

    private async Task SendHandshakeAsync()
    {
        await SendControlAsync(new JsonObject
        {
            ["t"] = "hello", ["proto"] = ProtocolConstants.Version, ["role"] = "camera", ["app"] = "AnyCam Emulator",
            ["version"] = "0.1.0", ["platform"] = "windows", ["os"] = Environment.OSVersion.VersionString,
            ["model"] = "Emulator", ["name"] = $"Emulator on {Environment.MachineName}", ["id"] = "emulator",
        });
        await SendControlAsync(Caps());
        await SendControlAsync(_state);
    }

    private static JsonObject Caps()
    {
        JsonArray Formats() =>
        [
            new JsonObject { ["width"] = 3840, ["height"] = 2160, ["maxFps"] = 30, ["hdr"] = false },
            new JsonObject { ["width"] = 1920, ["height"] = 1080, ["maxFps"] = 60, ["hdr"] = false },
            new JsonObject { ["width"] = 1280, ["height"] = 720, ["maxFps"] = 60, ["hdr"] = false },
        ];
        JsonObject Camera(string id, string name, string position, string lens, double zoom) => new()
        {
            ["id"] = id, ["name"] = name, ["position"] = position, ["lens"] = lens, ["zoomFactor"] = zoom,
            ["formats"] = Formats(), ["minISO"] = 32, ["maxISO"] = 3072, ["minShutter"] = 1.0 / 8000, ["maxShutter"] = 1.0 / 3,
            ["minZoom"] = 1, ["maxZoom"] = 10, ["hasTorch"] = position == "back", ["focus"] = true, ["manualFocus"] = true,
            ["stabilization"] = new JsonArray("off", "standard", "cinematic"), ["maxWBGain"] = 4.0, ["lowLightBoost"] = false,
        };
        return new JsonObject
        {
            ["t"] = "caps",
            ["cameras"] = new JsonArray(
                Camera("emulator-ultrawide", "Back Ultra Wide", "back", "ultraWide", 0.5),
                Camera("emulator-back", "Back Camera", "back", "wide", 1),
                Camera("emulator-tele", "Back Telephoto", "back", "telephoto", 3),
                Camera("emulator-front", "Front Camera", "front", "front", 1)),
            ["codecs"] = new JsonArray("h264"),
            ["audio"] = true,
            ["photo"] = true,
        };
    }

    private async Task ReceiveLoopAsync()
    {
        var header = new byte[ProtocolConstants.HeaderSize];
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                await _stream.ReadExactlyAsync(header, _cts.Token);
                var packet = PacketHeader.Read(header);
                var payload = new byte[packet.Length];
                await _stream.ReadExactlyAsync(payload, _cts.Token);
                if (packet.Type == PacketType.Control && JsonNode.Parse(payload) is JsonObject message)
                    await HandleAsync(message);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
        }
        Console.WriteLine("Host disconnected");
        Dispose();
    }

    private async Task HandleAsync(JsonObject message)
    {
        var type = message.MessageType();
        if (type != "ping")
            Console.WriteLine($"<- {message.ToJsonString()}");
        switch (type)
        {
            case "ping":
                message["t"] = "pong";
                await SendControlAsync(message);
                break;
            case "start":
                _streaming = true;
                _state["streaming"] = true;
                await SendControlAsync(_state);
                break;
            case "stop":
                _streaming = false;
                _state["streaming"] = false;
                await SendControlAsync(_state);
                break;
            case "configure":
                if (message["width"] is JsonValue w) _width = w.GetValue<int>();
                if (message["height"] is JsonValue h) _height = h.GetValue<int>();
                if (message["fps"] is JsonValue f) _fps = (int)Math.Round(f.GetValue<double>());
                if (message["bitrate"] is JsonValue b) _bitrate = (int)b.GetValue<long>();
                if (message["camera"] is JsonValue camera) _state["camera"] = camera.GetValue<string>();
                _state["width"] = _width;
                _state["height"] = _height;
                _state["fps"] = _fps;
                _state["bitrate"] = _bitrate;
                _restart = true;
                await SendControlAsync(_state);
                break;
            case "set":
                foreach (var (key, value) in message)
                {
                    if (key is not ("t" or "zoomRate"))
                        _state[key] = value?.DeepClone();
                }
                await SendControlAsync(_state);
                break;
            case "keyframe":
                _keyframeRequested = true;
                break;
            case "photo":
                var id = message["id"]?.GetValue<int>() ?? 0;
                var json = JsonSerializer.SerializeToUtf8Bytes(new { id, format = "jpeg", width = 64, height = 36 });
                var payload = new byte[4 + json.Length + TinyJpeg.Length];
                BinaryPrimitives.WriteUInt32LittleEndian(payload, (uint)json.Length);
                json.CopyTo(payload, 4);
                TinyJpeg.CopyTo(payload, 4 + json.Length);
                await SendPacketAsync(PacketType.Photo, 0, 0, payload);
                break;
        }
    }

    private async Task StatusLoopAsync()
    {
        var battery = 0.87;
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(2000, _cts.Token);
                battery = Math.Max(0.05, battery - 0.001);
                _state["battery"] = Math.Round(battery, 3);
                _state["exposureOffset"] = Math.Round(Random.Shared.NextDouble() * 0.2 - 0.1, 2);
                await SendControlAsync(_state);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (IOException)
            {
                return;
            }
        }
    }

    private void MediaLoop()
    {
        var clock = Stopwatch.StartNew();
        H264Encoder? encoder = null;
        TestPattern? pattern = null;
        byte[]? frame = null;
        long frameNumber = 0;
        var nextFrame = TimeSpan.Zero;
        var audioSamples = 0L;
        const int sampleRate = 48000;
        var audioAnnounced = false;

        try
        {
            while (!_cts.IsCancellationRequested)
            {
                if (!_streaming)
                {
                    Thread.Sleep(20);
                    nextFrame = clock.Elapsed;
                    audioSamples = (long)(clock.Elapsed.TotalSeconds * sampleRate);
                    continue;
                }

                if (encoder is null || _restart)
                {
                    _restart = false;
                    encoder?.Dispose();
                    encoder = new H264Encoder(_width, _height, _fps, _bitrate);
                    pattern = new TestPattern(_width, _height);
                    frame = new byte[pattern.FrameSize];
                    SendControlAsync(new JsonObject
                    {
                        ["t"] = "video", ["codec"] = "h264", ["width"] = _width, ["height"] = _height, ["fps"] = _fps,
                        ["bitrate"] = _bitrate, ["colorSpace"] = "bt709", ["range"] = "video",
                    }).Wait();
                    encoder.ForceKeyframe();
                }
                if (!audioAnnounced)
                {
                    SendControlAsync(new JsonObject { ["t"] = "audio", ["sampleRate"] = sampleRate, ["channels"] = 1, ["format"] = "s16le" }).Wait();
                    audioAnnounced = true;
                }

                var wait = nextFrame - clock.Elapsed;
                if (wait > TimeSpan.Zero)
                    Thread.Sleep(wait);
                // Like a real camera: timestamps follow the frame schedule, not the (jittery) wake-up time.
                var timestampUs = (long)(nextFrame.TotalMilliseconds * 1000);
                if (clock.Elapsed - nextFrame > TimeSpan.FromMilliseconds(200))
                    nextFrame = clock.Elapsed; // fell far behind: resync instead of bursting
                nextFrame += TimeSpan.FromSeconds(1.0 / _fps);
                var now = clock.Elapsed;

                // Audio up to "now": silence with a 1 kHz beep during the first 100 ms of every second.
                var targetSamples = (long)(now.TotalSeconds * sampleRate);
                if (targetSamples > audioSamples)
                {
                    var count = (int)Math.Min(targetSamples - audioSamples, sampleRate);
                    var pcm = new byte[count * 2];
                    for (var i = 0; i < count; i++)
                    {
                        var n = audioSamples + i;
                        var inBeep = n % sampleRate < sampleRate / 10;
                        var value = inBeep ? (short)(Math.Sin(2 * Math.PI * 1000 * n / sampleRate) * 8000) : (short)0;
                        BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), value);
                    }
                    var audioTimestamp = audioSamples * 1_000_000 / sampleRate;
                    audioSamples += count;
                    SendPacketAsync(PacketType.Audio, 0, audioTimestamp, pcm).Wait();
                }

                if (_keyframeRequested)
                {
                    _keyframeRequested = false;
                    encoder.ForceKeyframe();
                }

                pattern!.Render(frame!, frameNumber++, _fps, _state["zoom"]?.ToJsonString() ?? "");
                foreach (var (data, keyframe, _) in encoder.Encode(frame!, timestampUs * 10))
                {
                    // Mimic the phone's flow control: if the socket backs up, skip to the next keyframe.
                    if (Interlocked.Read(ref _queuedBytes) > 2_000_000 && !keyframe)
                        continue;
                    var flags = (byte)(keyframe ? (byte)(VideoFlags.Keyframe | VideoFlags.ParameterSets) : 0);
                    SendPacketAsync(PacketType.Video, flags, timestampUs, data).Wait();
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or AggregateException or OperationCanceledException)
        {
        }
        finally
        {
            encoder?.Dispose();
        }
    }

    private Task SendControlAsync(JsonObject message) =>
        SendPacketAsync(PacketType.Control, 0, 0, JsonSerializer.SerializeToUtf8Bytes(message));

    private async Task SendPacketAsync(PacketType type, byte flags, long timestamp, byte[] payload)
    {
        var packet = new byte[ProtocolConstants.HeaderSize + payload.Length];
        new PacketHeader(type, flags, payload.Length, timestamp).Write(packet);
        payload.CopyTo(packet, ProtocolConstants.HeaderSize);
        Interlocked.Add(ref _queuedBytes, packet.Length);
        await _sendLock.WaitAsync(_cts.Token);
        try
        {
            await _stream.WriteAsync(packet, _cts.Token);
        }
        finally
        {
            _sendLock.Release();
            Interlocked.Add(ref _queuedBytes, -packet.Length);
        }
    }

    public void Dispose()
    {
        if (_cts.IsCancellationRequested)
            return;
        _cts.Cancel();
        _client.Dispose();
    }
}
