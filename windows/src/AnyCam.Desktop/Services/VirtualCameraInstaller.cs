using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace AnyCam.Desktop.Services;

/// <summary>Runs the elevated install script for the virtual camera DLL that ships next to AnyCam.exe.</summary>
public static class VirtualCameraInstaller
{
    private static string Script => Path.Combine(AppContext.BaseDirectory, "install-virtual-camera.ps1");
    private static string BundledFolder => Path.Combine(AppContext.BaseDirectory, "VirtualCamera");

    public static bool IsBundled => File.Exists(Script) && File.Exists(Path.Combine(BundledFolder, "AnyCamVCam.dll"));

    /// <summary>Shows the UAC prompt and installs. Returns null on success, otherwise an error message.</summary>
    public static Task<string?> InstallAsync() => Task.Run(() =>
    {
        if (!IsBundled)
            return "This build doesn't include the virtual camera (VirtualCamera\\AnyCamVCam.dll is missing).";
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{Script}\" -Source \"{BundledFolder}\"",
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            process!.WaitForExit();
            return process.ExitCode == 0 ? null : $"The installer failed (exit code {process.ExitCode}).";
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return "Installation was cancelled.";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    });
}
