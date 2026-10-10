using System.IO;
using AnyCam.Core.Media;
using AnyCam.Desktop.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace AnyCam.Desktop.ViewModels;

/// <summary>PC-side picture processing: framing, color, LUT and monitoring overlays.</summary>
public sealed partial class PictureViewModel : ObservableObject
{
    private readonly MediaEngine _engine;
    private readonly AppSettings _settings;
    private bool _loading;

    public PictureViewModel(MediaEngine engine, AppSettings settings)
    {
        _engine = engine;
        _settings = settings;
        Load(settings.Processing);
        _engine.Settings = Build();
    }

    // Framing
    [ObservableProperty] private bool _mirror;
    [ObservableProperty] private bool _flip;
    [ObservableProperty] private int _rotation;
    [ObservableProperty] private double _digitalZoom = 1;
    [ObservableProperty] private double _panX;
    [ObservableProperty] private double _panY;

    // Color
    [ObservableProperty] private double _exposure;
    [ObservableProperty] private double _contrast = 1;
    [ObservableProperty] private double _saturation = 1;
    [ObservableProperty] private double _vibrance;
    [ObservableProperty] private double _temperature;
    [ObservableProperty] private double _tint;
    [ObservableProperty] private double _gamma = 1;
    [ObservableProperty] private double _brightness;
    [ObservableProperty] private string? _lutPath;
    [ObservableProperty] private double _lutAmount = 1;

    // Monitoring (preview only)
    [ObservableProperty] private bool _showGrid;
    [ObservableProperty] private bool _showZebras;
    [ObservableProperty] private bool _showPeaking;
    [ObservableProperty] private bool _showSafeAreas;
    [ObservableProperty] private bool _showVerticalGuide;

    // Output framing (lives here because it's part of ProcessingSettings)
    [ObservableProperty] private string _outputSize = "Same as camera";
    [ObservableProperty] private bool _fill = true;

    public string[] OutputSizes { get; } = ["Same as camera", "1920×1080", "1280×720", "3840×2160", "1080×1920 (vertical)", "720×1280 (vertical)"];

    public string RotationText => $"{Rotation}°";
    public string DigitalZoomText => $"{DigitalZoom:0.00}×";
    public string PanXText => $"{PanX:+0.00;-0.00;0}";
    public string PanYText => $"{PanY:+0.00;-0.00;0}";
    public string ExposureText => $"{Exposure:+0.0;-0.0;0} EV";
    public string ContrastText => $"{Contrast:0.00}";
    public string SaturationText => $"{Saturation:0.00}";
    public string VibranceText => $"{Vibrance:+0.00;-0.00;0}";
    public string TemperatureText => $"{Temperature:+0.00;-0.00;0}";
    public string TintText => $"{Tint:+0.00;-0.00;0}";
    public string GammaText => $"{Gamma:0.00}";
    public string BrightnessText => $"{Brightness:+0.00;-0.00;0}";
    public string LutAmountText => $"{LutAmount:P0}";
    public string LutName => string.IsNullOrEmpty(LutPath) ? "No LUT" : Path.GetFileNameWithoutExtension(LutPath);
    public bool HasLut => !string.IsNullOrEmpty(LutPath);

    private void Load(ProcessingSettings s)
    {
        _loading = true;
        Mirror = s.Mirror;
        Flip = s.Flip;
        Rotation = s.Rotation;
        DigitalZoom = s.Zoom;
        PanX = s.PanX;
        PanY = s.PanY;
        Exposure = s.Exposure;
        Contrast = s.Contrast;
        Saturation = s.Saturation;
        Vibrance = s.Vibrance;
        Temperature = s.Temperature;
        Tint = s.Tint;
        Gamma = s.Gamma;
        Brightness = s.Brightness;
        LutPath = s.LutPath;
        LutAmount = s.LutAmount;
        ShowGrid = s.ShowGrid;
        ShowZebras = s.ShowZebras;
        ShowPeaking = s.ShowPeaking;
        ShowSafeAreas = s.ShowSafeAreas;
        ShowVerticalGuide = s.ShowVerticalGuide;
        Fill = s.Fill;
        OutputSize = (s.OutputWidth, s.OutputHeight) switch
        {
            (0, _) or (_, 0) => OutputSizes[0],
            (1080, 1920) => "1080×1920 (vertical)",
            (720, 1280) => "720×1280 (vertical)",
            var (w, h) => $"{w}×{h}",
        };
        _loading = false;
    }

    private ProcessingSettings Build()
    {
        var (width, height) = OutputSize == OutputSizes[0] ? (0, 0) : CameraViewModel.ParseSize(OutputSize.Split(' ')[0]);
        return new ProcessingSettings
        {
            Mirror = Mirror,
            Flip = Flip,
            Rotation = Rotation,
            Zoom = DigitalZoom,
            PanX = PanX,
            PanY = PanY,
            OutputWidth = width,
            OutputHeight = height,
            Fill = Fill,
            Exposure = Exposure,
            Contrast = Contrast,
            Saturation = Saturation,
            Vibrance = Vibrance,
            Temperature = Temperature,
            Tint = Tint,
            Gamma = Gamma,
            Brightness = Brightness,
            LutPath = LutPath,
            LutAmount = LutAmount,
            ShowGrid = ShowGrid,
            ShowZebras = ShowZebras,
            ShowPeaking = ShowPeaking,
            ShowSafeAreas = ShowSafeAreas,
            ShowVerticalGuide = ShowVerticalGuide,
        };
    }

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (_loading || e.PropertyName is null || e.PropertyName.EndsWith("Text", StringComparison.Ordinal) ||
            e.PropertyName is nameof(LutName) or nameof(HasLut))
            return;

        // Every setting maps to the immutable ProcessingSettings consumed by the engine.
        var settings = Build();
        _engine.Settings = settings;
        _settings.Processing = settings;
        OnPropertyChanged(e.PropertyName + "Text");
        if (e.PropertyName == nameof(LutPath))
        {
            OnPropertyChanged(nameof(LutName));
            OnPropertyChanged(nameof(HasLut));
        }
    }

    [RelayCommand]
    private void RotateLeft() => Rotation = (Rotation + 270) % 360;

    [RelayCommand]
    private void RotateRight() => Rotation = (Rotation + 90) % 360;

    [RelayCommand]
    private void ResetFraming()
    {
        _loading = true;
        DigitalZoom = 1;
        PanX = 0;
        PanY = 0;
        _loading = false;
        OnPropertyChanged(nameof(DigitalZoom));
    }

    [RelayCommand]
    private void ResetColor()
    {
        _loading = true;
        Exposure = 0;
        Contrast = 1;
        Saturation = 1;
        Vibrance = 0;
        Temperature = 0;
        Tint = 0;
        Gamma = 1;
        Brightness = 0;
        _loading = false;
        OnPropertyChanged(nameof(Exposure));
    }

    [RelayCommand]
    private void LoadLut()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose a 3D LUT",
            Filter = "Cube LUT (*.cube)|*.cube|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog() == true)
            LutPath = dialog.FileName;
    }

    [RelayCommand]
    private void RemoveLut() => LutPath = null;
}
