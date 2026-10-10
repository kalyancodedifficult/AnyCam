using System.Net;
using System.Net.Sockets;

namespace AnyCam.Core.Transport;

/// <summary>Somewhere a camera can be reached: an IP address (Wi‑Fi / LAN) or an iPhone over USB.</summary>
public abstract record CameraEndpoint(string DisplayName)
{
    /// <summary>Stable identity used to remember the last device and avoid duplicates.</summary>
    public abstract string Key { get; }

    public abstract TransportKind Kind { get; }

    public abstract Task<Stream> OpenAsync(CancellationToken cancellationToken);
}

public sealed record NetworkEndpoint(string DisplayName, string Host, int Port, string? CameraId = null) : CameraEndpoint(DisplayName)
{
    public override string Key => CameraId is { Length: > 0 } ? $"id:{CameraId}" : $"tcp:{Host}:{Port}";

    public override TransportKind Kind =>
        IPAddress.TryParse(Host, out var address) && IPAddress.IsLoopback(address) ? TransportKind.Local : TransportKind.WiFi;

    public override async Task<Stream> OpenAsync(CancellationToken cancellationToken)
    {
        var client = new TcpClient { NoDelay = true, ReceiveBufferSize = 4 * 1024 * 1024 };
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(4));
            await client.ConnectAsync(Host, Port, timeout.Token).ConfigureAwait(false);
            return new NetworkStream(client.Client, ownsSocket: true);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }
}

public sealed record UsbEndpoint(string DisplayName, int DeviceId, string SerialNumber, int Port) : CameraEndpoint(DisplayName)
{
    public override string Key => $"usb:{SerialNumber}";

    public override TransportKind Kind => TransportKind.Usb;

    public override async Task<Stream> OpenAsync(CancellationToken cancellationToken) =>
        await Usbmux.ConnectAsync(DeviceId, Port, cancellationToken).ConfigureAwait(false);
}
