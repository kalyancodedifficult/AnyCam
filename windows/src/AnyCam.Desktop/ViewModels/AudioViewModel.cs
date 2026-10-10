using System.Collections.ObjectModel;
using AnyCam.Core.Media;
using AnyCam.Core.Session;
using AnyCam.Desktop.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AnyCam.Desktop.ViewModels;

/// <summary>Routing of the phone's microphone to a Windows output device.</summary>
public sealed partial class AudioViewModel : ObservableObject
{
    private const string DefaultDeviceName = "Default output device";

    private readonly CameraSession _session;
    private readonly AppSettings _settings;
    private bool _loading;

    public AudioViewModel(CameraSession session, AppSettings settings)
    {
        _session = session;
        _settings = settings;
        _loading = true;
        Enabled = settings.Audio.Enabled;
        DelayMs = settings.Audio.DelayMs;
        Volume = settings.Audio.Volume * 100;
        RefreshDevices();
        _loading = false;
        _session.AudioSettings = settings.Audio;
    }

    public ObservableCollection<AudioDevice> Devices { get; } = [];

    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private AudioDevice? _selectedDevice;
    [ObservableProperty] private double _delayMs;
    [ObservableProperty] private double _volume = 100;

    public string DelayText => $"{DelayMs:0} ms";
    public string VolumeText => $"{Volume:0}%";

    public bool HasVirtualCable => Devices.Any(d => d.Name.Contains("CABLE", StringComparison.OrdinalIgnoreCase) ||
                                                    d.Name.Contains("Virtual", StringComparison.OrdinalIgnoreCase));

    [RelayCommand]
    private void RefreshDevices()
    {
        var wasLoading = _loading;
        _loading = true;
        Devices.Clear();
        Devices.Add(new AudioDevice("", DefaultDeviceName));
        foreach (var device in AudioPlayback.OutputDevices())
            Devices.Add(device);
        SelectedDevice = Devices.FirstOrDefault(d => d.Id == (_settings.Audio.DeviceId ?? "")) ?? Devices[0];
        _loading = wasLoading;
        OnPropertyChanged(nameof(HasVirtualCable));
    }

    partial void OnEnabledChanged(bool value) => Apply();
    partial void OnSelectedDeviceChanged(AudioDevice? value) => Apply();

    partial void OnDelayMsChanged(double value)
    {
        OnPropertyChanged(nameof(DelayText));
        Apply();
    }

    partial void OnVolumeChanged(double value)
    {
        OnPropertyChanged(nameof(VolumeText));
        Apply();
    }

    private void Apply()
    {
        if (_loading)
            return;
        var audio = new AudioOutputSettings
        {
            Enabled = Enabled,
            DeviceId = string.IsNullOrEmpty(SelectedDevice?.Id) ? null : SelectedDevice.Id,
            DelayMs = (int)DelayMs,
            Volume = (float)(Volume / 100),
        };
        _settings.Audio = audio;
        _session.AudioSettings = audio;
    }
}
