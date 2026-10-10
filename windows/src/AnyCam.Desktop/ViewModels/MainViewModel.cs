using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AnyCam.Core.Media;
using AnyCam.Core.Protocol;
using AnyCam.Core.Session;
using AnyCam.Core.Transport;
using AnyCam.Core.VirtualCamera;
using AnyCam.Desktop.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AnyCam.Desktop.ViewModels;

public sealed partial class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly AppSettings _settings;
    private readonly MediaEngine _engine;
    private readonly CameraSession _session;
    private readonly DeviceDiscovery _discovery;
    private readonly DispatcherTimer _timer;
    private readonly List<CameraEndpoint> _manualEndpoints = [];
    private int _refreshQueued;
    private long _notificationExpires;
    private bool _autoConnectSuppressed;

    public MainViewModel(AppSettings settings)
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        _settings = settings;
        _engine = new MediaEngine();
        _session = new CameraSession(_engine) { PhotoFolder = settings.PhotoFolder, PreferredConfig = settings.Stream };
        _discovery = new DeviceDiscovery();

        Camera = new CameraViewModel(_session, settings);
        Picture = new PictureViewModel(_engine, settings);
        Audio = new AudioViewModel(_session, settings);

        _session.Changed += QueueRefresh;
        _session.Notification += message => _dispatcher.BeginInvoke(() => ShowNotification(message));
        _engine.Error += message => _dispatcher.BeginInvoke(() => ShowNotification(message));
        _engine.VirtualCameraConsumerChanged += _ => QueueRefresh();
        _discovery.Changed += () => _dispatcher.BeginInvoke(RefreshDevices);

        if (!string.IsNullOrWhiteSpace(settings.LastManualHost) && TryParseHost(settings.LastManualHost, out var manual))
            _manualEndpoints.Add(manual);

        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) => RefreshStats();
        _timer.Start();
    }

    public MediaEngine Engine => _engine;
    public CameraViewModel Camera { get; }
    public PictureViewModel Picture { get; }
    public AudioViewModel Audio { get; }
    public AppSettings Settings => _settings;

    public ObservableCollection<CameraEndpoint> Devices { get; } = [];

    [ObservableProperty] private CameraEndpoint? _selectedDevice;
    [ObservableProperty] private string _statusText = "Not connected";
    [ObservableProperty] private string _statusKind = "idle"; // idle | connecting | connected | live
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private bool _isActive; // connecting or connected
    [ObservableProperty] private string _connectButtonText = "Connect";
    [ObservableProperty] private string _statsText = "";
    [ObservableProperty] private string _phoneText = "";
    [ObservableProperty] private string _virtualCameraText = "Virtual camera: checking…";
    [ObservableProperty] private string _virtualCameraKind = "idle"; // idle | live | missing
    [ObservableProperty] private bool _virtualCameraNeedsInstall;
    [ObservableProperty] private bool _isRecording;
    [ObservableProperty] private string _recordingTime = "";
    [ObservableProperty] private string? _notification;
    [ObservableProperty] private string _devicesHint = "Searching for phones…";

    public string RecordingFolder
    {
        get => _settings.RecordingFolder;
        set
        {
            _settings.RecordingFolder = value;
            OnPropertyChanged();
        }
    }

    public string PhotoFolder
    {
        get => _settings.PhotoFolder;
        set
        {
            _settings.PhotoFolder = value;
            _session.PhotoFolder = value;
            OnPropertyChanged();
        }
    }

    public bool AutoConnect
    {
        get => _settings.AutoConnect;
        set
        {
            _settings.AutoConnect = value;
            OnPropertyChanged();
        }
    }

    public async Task InitializeAsync()
    {
        var (pixels, width, height) = SlateRenderer.Render(1920, 1080, "AnyCam", "Waiting for your phone. Open AnyCam on the iPhone and connect it here.");
        _engine.SetSlate(pixels, width, height);
        RefreshDevices();
        _discovery.Start();
        await EnsureVirtualCameraAsync();
    }

    // ---------------------------------------------------------------------------------------
    // Virtual camera

    private async Task EnsureVirtualCameraAsync()
    {
        var status = await Task.Run(VirtualCameraRegistration.EnsureCreated);
        VirtualCameraNeedsInstall = status == VirtualCameraStatus.NotInstalled;
        if (status != VirtualCameraStatus.Ready)
        {
            VirtualCameraKind = "missing";
            VirtualCameraText = status == VirtualCameraStatus.NotInstalled
                ? "Virtual camera not installed"
                : VirtualCameraRegistration.LastError ?? "Virtual camera unavailable";
        }
        else
        {
            VirtualCameraKind = "idle";
            VirtualCameraText = "Virtual camera ready";
        }
    }

    [RelayCommand]
    private async Task InstallVirtualCamera()
    {
        var error = await VirtualCameraInstaller.InstallAsync();
        if (error is not null)
            ShowNotification(error);
        await EnsureVirtualCameraAsync();
        if (VirtualCameraKind != "missing")
            ShowNotification("Virtual camera installed. Pick \"AnyCam\" as the camera in Teams, Meet, Zoom or OBS.");
    }

    // ---------------------------------------------------------------------------------------
    // Devices and connection

    private void RefreshDevices()
    {
        var endpoints = _discovery.Endpoints.Concat(_manualEndpoints).GroupBy(e => e.Key).Select(g => g.First()).ToList();
        var selectedKey = SelectedDevice?.Key;

        for (var i = Devices.Count - 1; i >= 0; i--)
        {
            if (!endpoints.Any(e => e.Key == Devices[i].Key) && Devices[i].Key != _session.Endpoint?.Key)
                Devices.RemoveAt(i);
        }
        foreach (var endpoint in endpoints)
        {
            var index = Devices.ToList().FindIndex(d => d.Key == endpoint.Key);
            if (index < 0)
                Devices.Add(endpoint);
            else if (Devices[index] != endpoint && Devices[index].Key != _session.Endpoint?.Key)
                Devices[index] = endpoint;
        }

        SelectedDevice = Devices.FirstOrDefault(d => d.Key == selectedKey)
                         ?? Devices.FirstOrDefault(d => d.Key == _settings.LastDeviceKey)
                         ?? Devices.FirstOrDefault();

        DevicesHint = Devices.Count > 0
            ? ""
            : _discovery.UsbAvailable
                ? "No phone found. Open AnyCam on your iPhone (same Wi-Fi), or plug it in with USB."
                : "No phone found. Open AnyCam on your iPhone on the same Wi-Fi. (For USB, install iTunes or Apple Devices.)";

        TryAutoConnect();
    }

    private void TryAutoConnect()
    {
        if (!_settings.AutoConnect || _autoConnectSuppressed || _session.Status != SessionStatus.Idle)
            return;
        // Only phones that are actually discovered right now (USB or Bonjour), never a remembered manual IP
        // that may be stale: those reconnect forever and would block connecting to the real phone.
        var discovered = _discovery.Endpoints;
        var target = discovered.FirstOrDefault(d => d.Key == _settings.LastDeviceKey)
                     ?? (discovered.Count(d => d is UsbEndpoint) == 1 ? discovered.First(d => d is UsbEndpoint) : null);
        if (target is not null)
        {
            SelectedDevice = target;
            Connect(target);
        }
    }

    [RelayCommand]
    private async Task ToggleConnect()
    {
        if (_session.Status != SessionStatus.Idle)
        {
            _autoConnectSuppressed = true;
            await _session.DisconnectAsync();
            return;
        }
        if (SelectedDevice is null)
        {
            ShowNotification("Pick a phone first, or use “Connect by IP”.");
            return;
        }
        Connect(SelectedDevice);
    }

    private void Connect(CameraEndpoint endpoint)
    {
        _autoConnectSuppressed = false;
        _settings.LastDeviceKey = endpoint.Key;
        _session.PreferredConfig = _settings.Stream;
        _session.Connect(endpoint);
        StatusKind = "connecting";
        StatusText = $"Connecting to {endpoint.DisplayName}…";
    }

    /// <summary>Connects to "host" or "host:port" typed by the user.</summary>
    public void ConnectToHost(string text)
    {
        if (!TryParseHost(text, out var endpoint))
        {
            ShowNotification("Enter an IP address like 192.168.1.23 (optionally :47200).");
            return;
        }
        _settings.LastManualHost = text.Trim();
        _manualEndpoints.RemoveAll(e => e.Key == endpoint.Key);
        _manualEndpoints.Add(endpoint);
        RefreshDevices();
        SelectedDevice = Devices.First(d => d.Key == endpoint.Key);
        _ = ReconnectAsync(endpoint);
    }

    private async Task ReconnectAsync(CameraEndpoint endpoint)
    {
        if (_session.Status != SessionStatus.Idle)
            await _session.DisconnectAsync();
        Connect(endpoint);
    }

    private static bool TryParseHost(string text, out NetworkEndpoint endpoint)
    {
        endpoint = null!;
        text = text.Trim();
        if (text.Length == 0)
            return false;
        var port = ProtocolConstants.DefaultPort;
        var host = text;
        var colon = text.LastIndexOf(':');
        if (colon > 0 && text.Count(c => c == ':') == 1)
        {
            if (!int.TryParse(text[(colon + 1)..], out port) || port is <= 0 or > 65535)
                return false;
            host = text[..colon];
        }
        if (Uri.CheckHostName(host) == UriHostNameType.Unknown)
            return false;
        endpoint = new NetworkEndpoint($"{host} (manual)", host, port);
        return true;
    }

    // ---------------------------------------------------------------------------------------
    // Recording, photos, snapshots

    [RelayCommand]
    private void ToggleRecord()
    {
        if (_session.IsRecording)
            _session.StopRecording();
        else if (_session.StartRecording(_settings.RecordingFolder) is { } path)
            ShowNotification($"Recording to {Path.GetFileName(path)} (original quality, no re-encode)");
        QueueRefresh();
    }

    [RelayCommand]
    private void CapturePhoto()
    {
        if (!IsConnected)
        {
            ShowNotification("Connect a phone to take a full-resolution photo.");
            return;
        }
        _session.PhotoFolder = _settings.PhotoFolder;
        _session.CapturePhoto();
        ShowNotification("Taking a photo on the phone…");
    }

    [RelayCommand]
    private async Task SaveSnapshot()
    {
        var snapshot = await _engine.CaptureProgramAsync();
        if (snapshot is not { } frame)
            return;
        try
        {
            Directory.CreateDirectory(_settings.PhotoFolder);
            var path = Path.Combine(_settings.PhotoFolder, $"AnyCam_Snapshot_{DateTime.Now:yyyyMMdd_HHmmss}.png");
            var bitmap = BitmapSource.Create(frame.Width, frame.Height, 96, 96, PixelFormats.Bgra32, null, frame.Pixels, frame.Width * 4);
            using var stream = File.Create(path);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            encoder.Save(stream);
            ShowNotification($"Snapshot saved: {path}");
        }
        catch (Exception ex)
        {
            ShowNotification($"Couldn't save the snapshot: {ex.Message}");
        }
    }

    [RelayCommand]
    private void OpenRecordingFolder() => OpenFolder(_settings.RecordingFolder);

    [RelayCommand]
    private void OpenPhotoFolder() => OpenFolder(_settings.PhotoFolder);

    [RelayCommand]
    private void BrowseRecordingFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Recordings folder", InitialDirectory = RecordingFolder };
        if (dialog.ShowDialog() == true)
            RecordingFolder = dialog.FolderName;
    }

    [RelayCommand]
    private void BrowsePhotoFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Photos folder", InitialDirectory = PhotoFolder };
        if (dialog.ShowDialog() == true)
            PhotoFolder = dialog.FolderName;
    }

    private static void OpenFolder(string folder)
    {
        Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
    }

    // ---------------------------------------------------------------------------------------
    // Preview interaction

    public void OnPreviewClicked(double x, double y)
    {
        if (IsConnected && _engine.TryMapPreviewPoint(x, y, out var point))
            _session.FocusAt(point);
    }

    public void OnPreviewWheel(int delta) => Camera.NudgeZoom(delta);

    // ---------------------------------------------------------------------------------------
    // Refresh

    private void QueueRefresh()
    {
        if (Interlocked.Exchange(ref _refreshQueued, 1) == 0)
        {
            _dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                Interlocked.Exchange(ref _refreshQueued, 0);
                Refresh();
            });
        }
    }

    private void Refresh()
    {
        var status = _session.Status;
        IsConnected = status == SessionStatus.Connected;
        IsActive = status != SessionStatus.Idle;
        ConnectButtonText = status == SessionStatus.Idle ? "Connect" : "Disconnect";

        var name = _session.Hello?.Name ?? _session.Endpoint?.DisplayName ?? "phone";
        switch (status)
        {
            case SessionStatus.Idle:
                StatusKind = "idle";
                StatusText = _session.StatusMessage ?? "Not connected";
                break;
            case SessionStatus.Connecting:
                StatusKind = "connecting";
                StatusText = $"Connecting to {_session.Endpoint?.DisplayName}…";
                break;
            case SessionStatus.Reconnecting:
                StatusKind = "connecting";
                StatusText = string.IsNullOrEmpty(_session.StatusMessage) ? "Reconnecting…" : $"Reconnecting… {_session.StatusMessage}";
                break;
            case SessionStatus.Connected:
                StatusKind = _session.VirtualCameraLive || _session.IsRecording ? "live" : "connected";
                var transport = _session.Transport switch
                {
                    TransportKind.Usb => "USB",
                    TransportKind.WiFi => "Wi-Fi",
                    _ => "Local",
                };
                StatusText = $"{name} · {transport}";
                break;
        }

        Camera.Apply(_session.Caps, _session.State, IsConnected);
        IsRecording = _session.IsRecording;

        var state = _session.State;
        if (IsConnected && state.Battery is { } battery)
        {
            var thermal = state.Thermal switch
            {
                "serious" => " · 🌡 hot",
                "critical" => " · 🌡 too hot!",
                "fair" => " · 🌡 warm",
                _ => "",
            };
            PhoneText = $"{(state.Charging == true ? "⚡" : "🔋")} {battery:P0}{thermal}";
        }
        else
        {
            PhoneText = "";
        }
    }

    private void RefreshStats()
    {
        var stats = _engine.Stats;
        if (IsConnected && _session.Video is { } video)
        {
            var codec = video.IsHevc ? "HEVC" : "H.264";
            var decode = stats.HardwareDecoding ? "GPU decode" : stats.Decoder.Length > 0 ? "CPU decode" : "";
            StatsText = $"{video.Width}×{video.Height} · {stats.DecodeFps:0} fps · {codec} · {_session.ReceiveMbps:0.0} Mb/s · {_session.RoundTripMs:0} ms RTT"
                        + (decode.Length > 0 ? $" · {decode}" : "")
                        + (stats.DroppedFrames > 0 ? $" · {stats.DroppedFrames} late frames skipped" : "");
        }
        else
        {
            StatsText = stats.Gpu.Length > 0 ? $"GPU: {stats.Gpu}" : "";
        }

        if (VirtualCameraKind != "missing")
        {
            if (stats.VirtualCamera is { } request)
            {
                VirtualCameraKind = "live";
                VirtualCameraText = $"Virtual camera in use · {request.Width}×{request.Height} {request.Fps:0} fps";
            }
            else
            {
                VirtualCameraKind = "idle";
                VirtualCameraText = "Virtual camera ready";
            }
        }

        RecordingTime = _session.IsRecording ? _session.RecordingDuration.ToString(@"hh\:mm\:ss") : "";
        if (Notification is not null && Environment.TickCount64 > _notificationExpires)
            Notification = null;
    }

    public void ShowNotification(string message)
    {
        Notification = message;
        _notificationExpires = Environment.TickCount64 + 6000;
    }

    public async ValueTask DisposeAsync()
    {
        _timer.Stop();
        _discovery.Dispose();
        await _session.DisposeAsync();
        _engine.Dispose();
        _settings.Save();
    }
}
