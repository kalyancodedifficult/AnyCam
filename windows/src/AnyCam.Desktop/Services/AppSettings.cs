using System.IO;
using System.Text.Json;
using AnyCam.Core.Media;
using AnyCam.Core.Protocol;
using AnyCam.Core.Session;

namespace AnyCam.Desktop.Services;

/// <summary>User settings, persisted as JSON in %AppData%\AnyCam\settings.json.</summary>
public sealed class AppSettings
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AnyCam");
    private static string FilePath => Path.Combine(Folder, "settings.json");

    public string? LastDeviceKey { get; set; }
    public string? LastManualHost { get; set; }
    public bool AutoConnect { get; set; } = true;

    public StreamConfig Stream { get; set; } = new()
    {
        Width = 1920,
        Height = 1080,
        Fps = 30,
        Codec = "h264",
        Bitrate = 16_000_000,
        Orientation = "auto",
        Audio = true,
        KeyframeInterval = 2,
    };

    public ProcessingSettings Processing { get; set; } = new();
    public AudioOutputSettings Audio { get; set; } = new();

    public string RecordingFolder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "AnyCam");

    public string PhotoFolder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "AnyCam");

    public double WindowLeft { get; set; } = double.NaN;
    public double WindowTop { get; set; } = double.NaN;
    public double WindowWidth { get; set; } = 1360;
    public double WindowHeight { get; set; } = 860;
    public bool WindowMaximized { get; set; }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Options) ?? new AppSettings();
        }
        catch
        {
            // Corrupt settings: start fresh rather than refusing to start.
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var temporary = FilePath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(this, Options));
            File.Move(temporary, FilePath, overwrite: true);
        }
        catch
        {
            // Not fatal.
        }
    }
}
