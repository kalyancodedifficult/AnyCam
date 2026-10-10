using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.MediaFoundation;

namespace AnyCam.Core.Media;

/// <summary>The D3D11 device shared by the decoder, the processing pipeline and the preview.</summary>
public sealed class GpuDevice : IDisposable
{
    private static readonly FeatureLevel[] FeatureLevels =
    [
        FeatureLevel.Level_11_1,
        FeatureLevel.Level_11_0,
        FeatureLevel.Level_10_1,
    ];

    private GpuDevice(ID3D11Device device, ID3D11DeviceContext context, IMFDXGIDeviceManager manager, bool videoSupport)
    {
        Device = device;
        Context = context;
        DeviceManager = manager;
        SupportsVideo = videoSupport;

        using var dxgiDevice = device.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDevice.GetAdapter();
        AdapterName = adapter.Description.Description;
    }

    public ID3D11Device Device { get; }
    public ID3D11DeviceContext Context { get; }
    public IMFDXGIDeviceManager DeviceManager { get; }
    public bool SupportsVideo { get; }
    public string AdapterName { get; }

    public static GpuDevice Create()
    {
        MediaPlatform.EnsureStarted();

        var flags = DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport;
        var videoSupport = true;
        var result = D3D11.D3D11CreateDevice(IntPtr.Zero, DriverType.Hardware, flags, FeatureLevels, out var device, out var context);
        if (result.Failure)
        {
            videoSupport = false;
            D3D11.D3D11CreateDevice(IntPtr.Zero, DriverType.Hardware, DeviceCreationFlags.BgraSupport, FeatureLevels, out device, out context).CheckError();
        }

        // The decoder (MF work queue threads) and our engine thread both use the immediate context.
        using (var multithread = device!.QueryInterface<ID3D11Multithread>())
            multithread.SetMultithreadProtected(true);

        var manager = MediaFactory.MFCreateDXGIDeviceManager();
        manager.ResetDevice(device).CheckError();
        return new GpuDevice(device, context!, manager, videoSupport);
    }

    public void Dispose()
    {
        DeviceManager.Dispose();
        Context.ClearState();
        Context.Flush();
        Context.Dispose();
        Device.Dispose();
    }
}

/// <summary>Process-wide Media Foundation startup.</summary>
public static class MediaPlatform
{
    private static int _started;

    public static void EnsureStarted()
    {
        if (Interlocked.Exchange(ref _started, 1) == 0)
            MediaFactory.MFStartup(useLightVersion: false).CheckError();
    }
}
