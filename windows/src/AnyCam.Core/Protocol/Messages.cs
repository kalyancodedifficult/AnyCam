using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace AnyCam.Core.Protocol;

public static class ProtocolJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public static T? As<T>(this JsonObject message) => message.Deserialize<T>(Options);

    public static string? MessageType(this JsonObject message) =>
        message.TryGetPropertyValue("t", out var node) && node is JsonValue value && value.TryGetValue(out string? type) ? type : null;
}

/// <summary>`hello` from the camera.</summary>
public sealed class CameraHello
{
    public int Proto { get; set; }
    public string? Role { get; set; }
    public string? App { get; set; }
    public string? Version { get; set; }
    public string? Platform { get; set; }
    public string? Os { get; set; }
    public string? Model { get; set; }
    public string? Name { get; set; }
    public string? Id { get; set; }
}

/// <summary>`caps`: what the phone can do.</summary>
public sealed class CameraCaps
{
    public List<CameraInfo> Cameras { get; set; } = [];
    public List<string> Codecs { get; set; } = [];
    public bool Audio { get; set; }
    public bool Photo { get; set; }
}

public sealed class CameraInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Position { get; set; } = "";
    public string Lens { get; set; } = "";
    public double ZoomFactor { get; set; } = 1;
    public List<CameraFormat> Formats { get; set; } = [];
    public double MinISO { get; set; }
    public double MaxISO { get; set; }
    public double MinShutter { get; set; }
    public double MaxShutter { get; set; }
    public double MinZoom { get; set; } = 1;
    public double MaxZoom { get; set; } = 1;
    public List<double> ZoomSwitchOver { get; set; } = [];
    public bool HasTorch { get; set; }
    public bool Focus { get; set; }
    public bool ManualFocus { get; set; }
    public List<string> Stabilization { get; set; } = [];
    public double MaxWBGain { get; set; } = 1;
    public bool LowLightBoost { get; set; }

    /// <summary>Short label for lens buttons, e.g. "0.5×", "1×", "3×", "Front".</summary>
    [JsonIgnore]
    public string ShortLabel => Lens switch
    {
        "front" => "Front",
        _ => $"{ZoomFactor:0.#}×",
    };
}

public sealed class CameraFormat
{
    public int Width { get; set; }
    public int Height { get; set; }
    public double MaxFps { get; set; }
    public bool Hdr { get; set; }
}

/// <summary>`state`: current settings and readings. Every field is optional (messages are partial).</summary>
public sealed class CameraState
{
    public bool? Streaming { get; set; }
    public string? Camera { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public double? Fps { get; set; }
    public string? Codec { get; set; }
    public long? Bitrate { get; set; }
    public string? Orientation { get; set; }
    public bool? Audio { get; set; }
    public double? Zoom { get; set; }
    public string? FocusMode { get; set; }
    public double? LensPosition { get; set; }
    public bool? SmoothFocus { get; set; }
    public bool? FaceDriven { get; set; }
    public string? ExposureMode { get; set; }
    public double? Iso { get; set; }
    public double? Shutter { get; set; }
    public double? Ev { get; set; }
    public double? ExposureOffset { get; set; }
    public string? WbMode { get; set; }
    public double? Temperature { get; set; }
    public double? Tint { get; set; }
    public double? Torch { get; set; }
    public string? Stabilization { get; set; }
    public bool? Hdr { get; set; }
    public bool? LowLightBoost { get; set; }
    public double? Battery { get; set; }
    public bool? Charging { get; set; }
    public string? Thermal { get; set; }
    public bool? Interrupted { get; set; }
}

/// <summary>`video`: the bitstream that follows.</summary>
public sealed class VideoStreamInfo
{
    public string Codec { get; set; } = "h264";
    public int Width { get; set; }
    public int Height { get; set; }
    public double Fps { get; set; } = 30;
    public long Bitrate { get; set; }
    public string? ColorSpace { get; set; }
    public string? Range { get; set; }

    [JsonIgnore]
    public bool IsHevc => string.Equals(Codec, "hevc", StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public bool IsFullRange => string.Equals(Range, "full", StringComparison.OrdinalIgnoreCase);
}

/// <summary>`audio`: PCM format of AUDIO packets.</summary>
public sealed class AudioStreamInfo
{
    public int SampleRate { get; set; } = 48000;
    public int Channels { get; set; } = 1;
    public string Format { get; set; } = "s16le";
}

/// <summary>`event` from the camera.</summary>
public sealed class CameraEvent
{
    public string Level { get; set; } = "info";
    public string? Code { get; set; }
    public string? Message { get; set; }
}

/// <summary>Photo metadata (first part of a PHOTO packet).</summary>
public sealed class PhotoInfo
{
    public int Id { get; set; }
    public string Format { get; set; } = "jpeg";
    public int Width { get; set; }
    public int Height { get; set; }
}

/// <summary>Builders for host → camera control messages.</summary>
public static class HostMessages
{
    public static JsonObject Hello(string hostName, string appVersion) => new()
    {
        ["t"] = "hello",
        ["proto"] = ProtocolConstants.Version,
        ["role"] = "host",
        ["app"] = "AnyCam Desktop",
        ["version"] = appVersion,
        ["platform"] = "windows",
        ["name"] = hostName,
    };

    public static JsonObject Configure(StreamConfig config)
    {
        var message = new JsonObject { ["t"] = "configure" };
        if (config.Camera is not null) message["camera"] = config.Camera;
        if (config.Width > 0) message["width"] = config.Width;
        if (config.Height > 0) message["height"] = config.Height;
        if (config.Fps > 0) message["fps"] = config.Fps;
        if (config.Codec is not null) message["codec"] = config.Codec;
        if (config.Bitrate > 0) message["bitrate"] = config.Bitrate;
        if (config.Orientation is not null) message["orientation"] = config.Orientation;
        if (config.Audio is not null) message["audio"] = config.Audio;
        if (config.KeyframeInterval > 0) message["keyframeInterval"] = config.KeyframeInterval;
        return message;
    }

    public static JsonObject Start() => new() { ["t"] = "start" };
    public static JsonObject Stop() => new() { ["t"] = "stop" };
    public static JsonObject Keyframe() => new() { ["t"] = "keyframe" };
    public static JsonObject Tally(bool live, bool recording) => new() { ["t"] = "tally", ["live"] = live, ["recording"] = recording };
    public static JsonObject Photo(int id, string format = "jpeg") => new() { ["t"] = "photo", ["id"] = id, ["format"] = format };
    public static JsonObject Ping(int id, long timestampUs) => new() { ["t"] = "ping", ["id"] = id, ["ts"] = timestampUs };
    public static JsonObject Dim(bool dim) => new() { ["t"] = "ui", ["dim"] = dim };

    public static JsonObject Focus(double x, double y, bool focus = true, bool expose = true) => new()
    {
        ["t"] = "focus",
        ["x"] = Math.Clamp(x, 0, 1),
        ["y"] = Math.Clamp(y, 0, 1),
        ["focus"] = focus,
        ["expose"] = expose,
    };

    /// <summary>`set` with one or more camera settings, e.g. Set(("zoom", 2.0), ("zoomRate", 2.0)).</summary>
    public static JsonObject Set(params (string Key, JsonNode? Value)[] values)
    {
        var message = new JsonObject { ["t"] = "set" };
        foreach (var (key, value) in values)
            message[key] = value;
        return message;
    }
}

/// <summary>Stream configuration requested by the host.</summary>
public sealed record StreamConfig
{
    public string? Camera { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public double Fps { get; init; }
    public string? Codec { get; init; }
    public long Bitrate { get; init; }
    public string? Orientation { get; init; }
    public bool? Audio { get; init; }
    public double KeyframeInterval { get; init; }
}
