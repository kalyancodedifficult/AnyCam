using AnyCam.Core.Protocol;
using AnyCam.Core.Transport;

namespace AnyCam.Core.Session;

/// <summary>
/// Keeps a list of reachable cameras: iPhones on USB (via usbmuxd) and AnyCam apps announced on the
/// local network (Bonjour). Raises <see cref="Changed"/> on a background thread.
/// </summary>
public sealed class DeviceDiscovery : IDisposable
{
    private readonly MdnsBrowser _mdns = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly object _lock = new();
    private IReadOnlyList<UsbmuxDevice> _usbDevices = [];
    private IReadOnlyList<DiscoveredCamera> _networkCameras = [];

    public DeviceDiscovery()
    {
        _mdns.CamerasChanged += cameras =>
        {
            lock (_lock)
                _networkCameras = cameras;
            Changed?.Invoke();
        };
    }

    public event Action? Changed;

    /// <summary>True when Apple Mobile Device Service answers (USB is possible).</summary>
    public bool UsbAvailable { get; private set; }

    public IReadOnlyList<CameraEndpoint> Endpoints
    {
        get
        {
            lock (_lock)
            {
                var endpoints = new List<CameraEndpoint>();
                var usbIndex = 1;
                foreach (var device in _usbDevices.Where(d => d.IsUsb))
                {
                    var suffix = device.SerialNumber.Length > 6 ? device.SerialNumber[^6..] : device.SerialNumber;
                    var name = _usbDevices.Count(d => d.IsUsb) > 1 ? $"iPhone {usbIndex++} (USB · {suffix})" : "iPhone (USB)";
                    endpoints.Add(new UsbEndpoint(name, device.DeviceId, device.SerialNumber, ProtocolConstants.DefaultPort));
                }
                endpoints.AddRange(_networkCameras.Select(c => c.ToEndpoint()));
                return endpoints;
            }
        }
    }

    public void Start()
    {
        _mdns.Start();
        _ = Task.Run(() => PollUsbAsync(_cts.Token));
    }

    private async Task PollUsbAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            IReadOnlyList<UsbmuxDevice> devices;
            bool available;
            try
            {
                devices = await Usbmux.ListDevicesAsync(token).ConfigureAwait(false);
                available = true;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                devices = [];
                available = false;
            }

            bool changed;
            lock (_lock)
            {
                changed = available != UsbAvailable ||
                          !devices.Select(d => d.SerialNumber).SequenceEqual(_usbDevices.Select(d => d.SerialNumber));
                _usbDevices = devices;
                UsbAvailable = available;
            }
            if (changed)
                Changed?.Invoke();

            try
            {
                await Task.Delay(available ? 2000 : 5000, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _mdns.Dispose();
        _cts.Dispose();
    }
}
