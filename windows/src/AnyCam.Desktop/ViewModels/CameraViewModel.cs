using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json.Nodes;
using AnyCam.Core.Protocol;
using AnyCam.Core.Session;
using AnyCam.Desktop.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AnyCam.Desktop.ViewModels;

/// <summary>Remote control of the phone camera (lens, format, focus, exposure, white balance…).</summary>
public sealed partial class CameraViewModel : ObservableObject
{
    private static readonly (string Label, double Seconds)[] ShutterOptions =
    [
        ("1/24", 1 / 24.0), ("1/25", 1 / 25.0), ("1/30", 1 / 30.0), ("1/48", 1 / 48.0), ("1/50", 1 / 50.0),
        ("1/60", 1 / 60.0), ("1/96", 1 / 96.0), ("1/100", 1 / 100.0), ("1/120", 1 / 120.0), ("1/125", 1 / 125.0),
        ("1/200", 1 / 200.0), ("1/250", 1 / 250.0), ("1/500", 1 / 500.0), ("1/1000", 1 / 1000.0), ("1/2000", 1 / 2000.0),
    ];

    private static readonly int[] StandardFrameRates = [24, 25, 30, 48, 50, 60];

    private readonly CameraSession _session;
    private readonly AppSettings _settings;
    private readonly Throttler _throttler = new(TimeSpan.FromMilliseconds(60));
    private readonly Throttler _slowThrottler = new(TimeSpan.FromMilliseconds(600));
    private readonly Dictionary<string, long> _touched = new();
    private bool _applying;

    public CameraViewModel(CameraSession session, AppSettings settings)
    {
        _session = session;
        _settings = settings;
        var stream = settings.Stream;
        _resolution = FormatSize(stream.Width, stream.Height);
        _frameRate = (int)Math.Round(stream.Fps);
        _codec = stream.Codec == "hevc" ? "HEVC" : "H.264";
        _bitrateMbps = stream.Bitrate / 1_000_000.0;
        _orientation = Capitalize(stream.Orientation ?? "auto");
        _sendAudio = stream.Audio ?? true;
        Resolutions.Add(_resolution);
        FrameRates.Add(_frameRate);
    }

    public ObservableCollection<CameraInfo> Lenses { get; } = [];
    public ObservableCollection<string> Resolutions { get; } = [];
    public ObservableCollection<int> FrameRates { get; } = [];
    public ObservableCollection<string> Codecs { get; } = ["H.264"];
    public string[] Orientations { get; } = ["Auto", "Landscape", "Portrait"];
    public ObservableCollection<string> StabilizationModes { get; } = ["off"];
    public string[] Shutters { get; } = ShutterOptions.Select(o => o.Label).ToArray();

    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private CameraInfo? _selectedLens;
    [ObservableProperty] private string _resolution;
    [ObservableProperty] private int _frameRate;
    [ObservableProperty] private string _codec;
    [ObservableProperty] private double _bitrateMbps;
    [ObservableProperty] private string _orientation;
    [ObservableProperty] private bool _sendAudio;

    [ObservableProperty] private double _zoom = 1;
    [ObservableProperty] private double _minZoom = 1;
    [ObservableProperty] private double _maxZoom = 10;

    [ObservableProperty] private string _focusMode = "continuous";
    [ObservableProperty] private double _lensPosition = 0.5;
    [ObservableProperty] private bool _smoothFocus = true;
    [ObservableProperty] private bool _faceDriven = true;
    [ObservableProperty] private bool _manualFocusSupported = true;

    [ObservableProperty] private string _exposureMode = "auto";
    [ObservableProperty] private double _ev;
    [ObservableProperty] private double _iso = 100;
    [ObservableProperty] private double _minIso = 25;
    [ObservableProperty] private double _maxIso = 3200;
    [ObservableProperty] private string _shutter = "1/50";
    [ObservableProperty] private string _exposureReadout = "";

    [ObservableProperty] private string _wbMode = "auto";
    [ObservableProperty] private double _temperature = 5500;
    [ObservableProperty] private double _tint;
    [ObservableProperty] private string _wbReadout = "";

    [ObservableProperty] private double _torch;
    [ObservableProperty] private bool _hasTorch;
    [ObservableProperty] private string _stabilization = "off";
    [ObservableProperty] private bool _hdr;
    [ObservableProperty] private bool _lowLightBoost;
    [ObservableProperty] private bool _dimPhone;

    public string BitrateText => $"{BitrateMbps:0} Mb/s";
    public string ZoomText => $"{Zoom:0.0}×";
    public string LensPositionText => LensPosition < 0.02 ? "Near" : LensPosition > 0.98 ? "Far" : $"{LensPosition:0.00}";
    public string EvText => Ev == 0 ? "0 EV" : $"{Ev:+0.0;-0.0} EV";
    public string IsoText => $"ISO {Iso:0}";
    public string TemperatureText => $"{Temperature:0} K";
    public string TintText => Tint == 0 ? "0" : $"{Tint:+0;-0}";
    public string TorchText => Torch <= 0.01 ? "Off" : $"{Torch:P0}";

    // ---------------------------------------------------------------------------------------
    // Applying phone state to the UI

    /// <summary>Refreshes the UI from the latest caps/state (UI thread).</summary>
    public void Apply(CameraCaps? caps, CameraState state, bool connected)
    {
        _applying = true;
        try
        {
            IsConnected = connected;
            if (caps is not null)
                SyncLenses(caps);

            var lens = Lenses.FirstOrDefault(l => l.Id == state.Camera) ?? SelectedLens ?? Lenses.FirstOrDefault(l => l.Lens == "wide");
            if (lens is not null && !ReferenceEquals(lens, SelectedLens))
                SelectedLens = lens;
            if (SelectedLens is not null)
                UpdateLensOptions(SelectedLens, caps);

            if (state.Width is > 0 && state.Height is > 0 && !Recent("resolution"))
                SetResolution(FormatSize(state.Width.Value, state.Height.Value));
            if (state.Fps is > 0 && !Recent("fps"))
                SetFrameRate((int)Math.Round(state.Fps.Value));
            if (state.Codec is not null && !Recent("codec"))
                Codec = state.Codec == "hevc" ? "HEVC" : "H.264";
            if (state.Bitrate is > 0 && !Recent("bitrate"))
                BitrateMbps = Math.Round(state.Bitrate.Value / 1_000_000.0);
            if (state.Orientation is not null && !Recent("orientation"))
                Orientation = Capitalize(state.Orientation);
            if (state.Audio is not null && !Recent("audio"))
                SendAudio = state.Audio.Value;

            if (state.Zoom is not null && !Recent("zoom"))
                Zoom = state.Zoom.Value;
            if (state.FocusMode is not null && !Recent("focusMode"))
                FocusMode = state.FocusMode;
            if (state.LensPosition is not null && !Recent("lensPosition"))
                LensPosition = state.LensPosition.Value;
            if (state.SmoothFocus is not null && !Recent("smoothFocus"))
                SmoothFocus = state.SmoothFocus.Value;
            if (state.FaceDriven is not null && !Recent("faceDriven"))
                FaceDriven = state.FaceDriven.Value;

            if (state.ExposureMode is not null && !Recent("exposureMode"))
                ExposureMode = state.ExposureMode;
            if (state.Ev is not null && !Recent("ev"))
                Ev = state.Ev.Value;
            if (state.Iso is not null && !Recent("iso"))
                Iso = state.Iso.Value;
            if (state.Shutter is > 0 && !Recent("shutter") && ExposureMode is "manual" or "shutterPriority")
                Shutter = ClosestShutter(state.Shutter.Value);
            ExposureReadout = FormatExposure(state);

            if (state.WbMode is not null && !Recent("wbMode"))
                WbMode = state.WbMode;
            if (state.Temperature is not null && !Recent("temperature"))
                Temperature = Math.Round(state.Temperature.Value);
            if (state.Tint is not null && !Recent("tint"))
                Tint = Math.Round(state.Tint.Value);
            WbReadout = state.Temperature is { } kelvin ? $"Now {kelvin:0} K, tint {state.Tint ?? 0:+0;-0;0}" : "";

            if (state.Torch is not null && !Recent("torch"))
                Torch = state.Torch.Value;
            if (state.Stabilization is not null && !Recent("stabilization"))
                Stabilization = state.Stabilization;
            if (state.Hdr is not null && !Recent("hdr"))
                Hdr = state.Hdr.Value;
            if (state.LowLightBoost is not null && !Recent("lowLightBoost"))
                LowLightBoost = state.LowLightBoost.Value;
        }
        finally
        {
            _applying = false;
        }
    }

    private void SyncLenses(CameraCaps caps)
    {
        // Only rebuild when the set of lenses changes: replacing items would reset the list selection.
        var ordered = caps.Cameras.OrderBy(c => c.Lens == "front" ? 1 : 0).ThenBy(c => c.ZoomFactor).ToList();
        if (!ordered.Select(c => c.Id).SequenceEqual(Lenses.Select(l => l.Id)))
        {
            Lenses.Clear();
            foreach (var camera in ordered)
                Lenses.Add(camera);
        }

        var codecs = caps.Codecs.Contains("hevc") ? new[] { "H.264", "HEVC" } : ["H.264"];
        if (!codecs.SequenceEqual(Codecs))
            Codecs.SyncTo(codecs);
    }

    private void UpdateLensOptions(CameraInfo lens, CameraCaps? caps)
    {
        var sizes = lens.Formats
            .Where(f => f.Width * 9 == f.Height * 16)
            .OrderBy(f => f.Width)
            .Select(f => FormatSize(f.Width, f.Height))
            .Distinct()
            .ToList();
        if (sizes.Count == 0)
            sizes.Add("1920×1080");
        if (!sizes.SequenceEqual(Resolutions))
        {
            var current = Resolution;
            Resolutions.SyncTo(sizes);
            SetResolution(sizes.Contains(current) ? current : sizes.Contains("1920×1080") ? "1920×1080" : sizes[^1]);
        }
        UpdateFrameRates(lens);

        MinZoom = Math.Max(lens.MinZoom, 1);
        MaxZoom = Math.Max(MinZoom + 0.1, Math.Min(lens.MaxZoom, 15));
        MinIso = lens.MinISO > 0 ? lens.MinISO : 25;
        MaxIso = lens.MaxISO > MinIso ? lens.MaxISO : 3200;
        HasTorch = lens.HasTorch;
        ManualFocusSupported = lens.ManualFocus;

        var modes = lens.Stabilization.Count > 0 ? lens.Stabilization : ["off"];
        if (!modes.SequenceEqual(StabilizationModes))
            StabilizationModes.SyncTo(modes);
    }

    private void UpdateFrameRates(CameraInfo? lens)
    {
        var (width, height) = ParseSize(Resolution);
        var maxFps = lens?.Formats.Where(f => f.Width == width && f.Height == height).Select(f => f.MaxFps).DefaultIfEmpty(30).Max() ?? 60;
        var rates = StandardFrameRates.Where(r => r <= maxFps + 0.5).ToList();
        if (!rates.SequenceEqual(FrameRates))
        {
            var current = FrameRate;
            FrameRates.SyncTo(rates);
            SetFrameRate(rates.Contains(current) ? current : rates.Contains(30) ? 30 : rates[^1]);
        }
    }

    private void SetResolution(string value)
    {
        if (!Resolutions.Contains(value))
            Resolutions.Add(value);
        Resolution = value;
    }

    private void SetFrameRate(int value)
    {
        if (!FrameRates.Contains(value))
            FrameRates.Add(value);
        FrameRate = value;
    }

    private static string FormatExposure(CameraState state)
    {
        var parts = new List<string>();
        if (state.Iso is { } iso)
            parts.Add($"ISO {iso:0}");
        if (state.Shutter is > 0)
            parts.Add(state.Shutter.Value >= 0.5 ? $"{state.Shutter.Value:0.0}s" : $"1/{Math.Round(1 / state.Shutter.Value):0}");
        if (state.ExposureOffset is { } offset)
            parts.Add($"meter {offset:+0.0;-0.0;0.0} EV");
        return parts.Count == 0 ? "" : "Now " + string.Join(" · ", parts);
    }

    private static string ClosestShutter(double seconds) =>
        ShutterOptions.OrderBy(o => Math.Abs(Math.Log(o.Seconds / seconds))).First().Label;

    private static double ShutterSeconds(string label) =>
        ShutterOptions.FirstOrDefault(o => o.Label == label).Seconds is var s && s > 0 ? s : 1 / 50.0;

    internal static string FormatSize(int width, int height) => $"{width}×{height}";

    internal static (int Width, int Height) ParseSize(string text)
    {
        var parts = text.Split('×', 'x');
        return parts.Length == 2 && int.TryParse(parts[0], out var w) && int.TryParse(parts[1], out var h) ? (w, h) : (1920, 1080);
    }

    private static string Capitalize(string text) =>
        string.IsNullOrEmpty(text) ? text : char.ToUpper(text[0], CultureInfo.InvariantCulture) + text[1..];

    // ---------------------------------------------------------------------------------------
    // Sending user changes to the phone

    private bool Recent(string key) => _touched.TryGetValue(key, out var tick) && Environment.TickCount64 - tick < 1500;

    private void Touch(string key) => _touched[key] = Environment.TickCount64;

    private void Send(string key, params (string Key, JsonNode? Value)[] values)
    {
        if (_applying)
            return;
        Touch(key);
        _throttler.Run(key, () => _session.Set(values));
    }

    private void Configure(string key, StreamConfig config, bool slow = false)
    {
        if (_applying)
            return;
        Touch(key);
        var stream = _settings.Stream;
        _settings.Stream = stream with
        {
            Camera = config.Camera ?? stream.Camera,
            Width = config.Width > 0 ? config.Width : stream.Width,
            Height = config.Height > 0 ? config.Height : stream.Height,
            Fps = config.Fps > 0 ? config.Fps : stream.Fps,
            Codec = config.Codec ?? stream.Codec,
            Bitrate = config.Bitrate > 0 ? config.Bitrate : stream.Bitrate,
            Orientation = config.Orientation ?? stream.Orientation,
            Audio = config.Audio ?? stream.Audio,
        };
        _session.PreferredConfig = _settings.Stream;
        if (slow)
            _slowThrottler.Run(key, () => _session.Configure(config));
        else
            _session.Configure(config);
    }

    partial void OnSelectedLensChanged(CameraInfo? value)
    {
        if (value is null || _applying)
            return;
        UpdateLensOptions(value, null);
        Configure("camera", new StreamConfig { Camera = value.Id });
    }

    partial void OnResolutionChanged(string value)
    {
        if (string.IsNullOrEmpty(value))
            return; // transient null from a ComboBox while its list changes
        UpdateFrameRates(SelectedLens);
        var (width, height) = ParseSize(value);
        Configure("resolution", new StreamConfig { Width = width, Height = height });
    }

    partial void OnFrameRateChanged(int value)
    {
        if (value > 0)
            Configure("fps", new StreamConfig { Fps = value });
    }

    partial void OnCodecChanged(string value)
    {
        if (!string.IsNullOrEmpty(value))
            Configure("codec", new StreamConfig { Codec = value == "HEVC" ? "hevc" : "h264" });
    }

    partial void OnBitrateMbpsChanged(double value)
    {
        OnPropertyChanged(nameof(BitrateText));
        Configure("bitrate", new StreamConfig { Bitrate = (long)(value * 1_000_000) }, slow: true);
    }

    partial void OnOrientationChanged(string value)
    {
        if (!string.IsNullOrEmpty(value))
            Configure("orientation", new StreamConfig { Orientation = value.ToLowerInvariant() });
    }

    partial void OnSendAudioChanged(bool value) => Configure("audio", new StreamConfig { Audio = value });

    partial void OnZoomChanged(double value)
    {
        OnPropertyChanged(nameof(ZoomText));
        Send("zoom", ("zoom", Math.Round(value, 2)));
    }

    partial void OnFocusModeChanged(string value)
    {
        if (value == "manual")
            Send("focusMode", ("focusMode", value), ("lensPosition", LensPosition));
        else
            Send("focusMode", ("focusMode", value));
    }

    partial void OnLensPositionChanged(double value)
    {
        OnPropertyChanged(nameof(LensPositionText));
        if (_applying)
            return;
        if (FocusMode != "manual")
        {
            _applying = true;
            FocusMode = "manual";
            _applying = false;
            Touch("focusMode");
        }
        Send("lensPosition", ("focusMode", "manual"), ("lensPosition", Math.Round(value, 3)));
    }

    partial void OnSmoothFocusChanged(bool value) => Send("smoothFocus", ("smoothFocus", value));
    partial void OnFaceDrivenChanged(bool value) => Send("faceDriven", ("faceDriven", value));

    partial void OnExposureModeChanged(string value)
    {
        switch (value)
        {
            case "manual":
                Send("exposureMode", ("exposureMode", value), ("iso", Iso), ("shutter", ShutterSeconds(Shutter)));
                break;
            case "shutterPriority":
                Send("exposureMode", ("exposureMode", value), ("shutter", ShutterSeconds(Shutter)));
                break;
            default:
                Send("exposureMode", ("exposureMode", value));
                break;
        }
    }

    partial void OnEvChanged(double value)
    {
        OnPropertyChanged(nameof(EvText));
        Send("ev", ("ev", Math.Round(value, 2)));
    }

    partial void OnIsoChanged(double value)
    {
        OnPropertyChanged(nameof(IsoText));
        if (ExposureMode == "manual")
            Send("iso", ("exposureMode", "manual"), ("iso", Math.Round(value)), ("shutter", ShutterSeconds(Shutter)));
    }

    partial void OnShutterChanged(string value)
    {
        if (ExposureMode == "manual")
            Send("shutter", ("exposureMode", "manual"), ("iso", Math.Round(Iso)), ("shutter", ShutterSeconds(value)));
        else if (ExposureMode == "shutterPriority")
            Send("shutter", ("exposureMode", "shutterPriority"), ("shutter", ShutterSeconds(value)));
    }

    partial void OnWbModeChanged(string value)
    {
        if (value == "manual")
            Send("wbMode", ("wbMode", value), ("temperature", Temperature), ("tint", Tint));
        else
            Send("wbMode", ("wbMode", value));
    }

    partial void OnTemperatureChanged(double value)
    {
        OnPropertyChanged(nameof(TemperatureText));
        SendWhiteBalance("temperature");
    }

    partial void OnTintChanged(double value)
    {
        OnPropertyChanged(nameof(TintText));
        SendWhiteBalance("tint");
    }

    private void SendWhiteBalance(string key)
    {
        if (_applying)
            return;
        if (WbMode != "manual")
        {
            _applying = true;
            WbMode = "manual";
            _applying = false;
            Touch("wbMode");
        }
        Send(key, ("wbMode", "manual"), ("temperature", Math.Round(Temperature)), ("tint", Math.Round(Tint)));
    }

    [RelayCommand]
    private void WhiteBalancePreset(string kelvin)
    {
        _applying = true;
        Tint = 0;
        _applying = false;
        Temperature = double.Parse(kelvin, CultureInfo.InvariantCulture);
    }

    partial void OnTorchChanged(double value)
    {
        OnPropertyChanged(nameof(TorchText));
        Send("torch", ("torch", Math.Round(value, 2)));
    }

    partial void OnStabilizationChanged(string value)
    {
        if (!string.IsNullOrEmpty(value))
            Send("stabilization", ("stabilization", value));
    }
    partial void OnHdrChanged(bool value) => Send("hdr", ("hdr", value));
    partial void OnLowLightBoostChanged(bool value) => Send("lowLightBoost", ("lowLightBoost", value));

    partial void OnDimPhoneChanged(bool value)
    {
        if (!_applying)
            _session.SetPhoneDimmed(value);
    }

    /// <summary>Mouse wheel over the preview: zoom the phone camera in or out.</summary>
    public void NudgeZoom(int wheelDelta)
    {
        if (!IsConnected)
            return;
        var factor = Math.Pow(1.1, wheelDelta / 120.0);
        Zoom = Math.Clamp(Math.Round(Zoom * factor, 2), MinZoom, MaxZoom);
    }
}
