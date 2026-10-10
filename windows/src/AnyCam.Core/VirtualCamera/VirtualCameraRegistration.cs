using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace AnyCam.Core.VirtualCamera;

public enum VirtualCameraStatus
{
    /// <summary>AnyCamVCam.dll isn't registered (run the installer / regsvr32 as administrator).</summary>
    NotInstalled,
    /// <summary>Registered, but creating the camera failed (see <see cref="VirtualCameraRegistration.LastError"/>).</summary>
    Failed,
    /// <summary>The "AnyCam" camera is visible to apps.</summary>
    Ready,
}

/// <summary>
/// Creates the "AnyCam" virtual camera through the helper exported by AnyCamVCam.dll
/// (MFCreateVirtualCamera, system lifetime, current user — no admin rights needed).
/// </summary>
public static class VirtualCameraRegistration
{
    public const string Clsid = "{E39221D4-60F2-41F3-B09B-3156B16205BB}";
    public const string FriendlyName = "AnyCam";

    public static string? LastError { get; private set; }

    /// <summary>Path of the registered AnyCamVCam.dll (HKLM), or null.</summary>
    public static string? RegisteredDllPath()
    {
        using var key = Registry.LocalMachine.OpenSubKey($@"SOFTWARE\Classes\CLSID\{Clsid}\InprocServer32");
        return key?.GetValue(null) as string is { Length: > 0 } path && File.Exists(path) ? path : null;
    }

    /// <summary>Makes sure the virtual camera exists and is started. Safe to call on every launch.</summary>
    public static VirtualCameraStatus EnsureCreated()
    {
        var path = RegisteredDllPath();
        if (path is null)
        {
            LastError = "The AnyCam virtual camera driver isn't installed.";
            return VirtualCameraStatus.NotInstalled;
        }

        var hr = Invoke(path, register: true);
        if (hr >= 0)
        {
            LastError = null;
            return VirtualCameraStatus.Ready;
        }

        LastError = hr == unchecked((int)0x80070005)
            ? "Windows denied camera access. Turn on Settings › Privacy & security › Camera › \"Let desktop apps access your camera\"."
            : $"Creating the virtual camera failed (0x{hr:X8}).";
        return VirtualCameraStatus.Failed;
    }

    /// <summary>Removes the virtual camera (it stays installed and can be re-created).</summary>
    public static bool Remove()
    {
        var path = RegisteredDllPath();
        return path is not null && Invoke(path, register: false) >= 0;
    }

    private static unsafe int Invoke(string dllPath, bool register)
    {
        // Run on an MTA thread: MFCreateVirtualCamera may block on the camera consent broker.
        var result = 0;
        var thread = new Thread(() =>
        {
            nint module = 0;
            try
            {
                module = NativeLibrary.Load(dllPath);
                fixed (char* name = FriendlyName)
                {
                    if (register)
                    {
                        var fn = (delegate* unmanaged[Stdcall]<int, int, char*, nint*, int>)NativeLibrary.GetExport(module, "AnyCamVCam_Register");
                        result = fn(1, 0, name, null);
                    }
                    else
                    {
                        var fn = (delegate* unmanaged[Stdcall]<int, int, char*, int>)NativeLibrary.GetExport(module, "AnyCamVCam_Remove");
                        result = fn(1, 0, name);
                    }
                }
            }
            catch (Exception ex)
            {
                result = ex.HResult < 0 ? ex.HResult : unchecked((int)0x80004005);
            }
            // The DLL stays loaded: the Frame Server and apps load it anyway and unloading it while
            // MF might still reference it is pointless.
        });
        thread.SetApartmentState(ApartmentState.MTA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join();
        return result;
    }
}
