using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace AnyCam.Core.Transport;

/// <summary>An iOS device known to usbmuxd.</summary>
public sealed record UsbmuxDevice(int DeviceId, string SerialNumber, string ConnectionType, int ProductId)
{
    public bool IsUsb => string.Equals(ConnectionType, "USB", StringComparison.OrdinalIgnoreCase);
}

public sealed class UsbmuxException(string message, int resultCode = -1) : Exception(message)
{
    public int ResultCode { get; } = resultCode;
}

/// <summary>
/// Client for Apple's usbmuxd protocol. On Windows it is served by "Apple Mobile Device Service"
/// (installed with iTunes or the Apple Devices app) on 127.0.0.1:27015. A successful Connect turns
/// the socket into a raw TCP tunnel to a port on the iPhone — the same port AnyCam listens on for Wi‑Fi.
/// </summary>
public static class Usbmux
{
    public const int ServicePort = 27015;
    private const int PlistVersion = 1;
    private const int PlistMessage = 8;

    // Result codes from usbmuxd.
    public const int ResultOk = 0;
    public const int ResultBadDevice = 2;
    public const int ResultConnectionRefused = 3;

    public static async Task<bool> IsServiceRunningAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = await OpenAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static async Task<IReadOnlyList<UsbmuxDevice>> ListDevicesAsync(CancellationToken cancellationToken = default)
    {
        using var client = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var stream = client.GetStream();
        await SendAsync(stream, Request("ListDevices"), 1, cancellationToken).ConfigureAwait(false);
        var reply = await ReceiveAsync(stream, cancellationToken).ConfigureAwait(false);

        var devices = new List<UsbmuxDevice>();
        if (reply.TryGetValue("DeviceList", out var list) && list is List<object> items)
        {
            foreach (var item in items.OfType<Dictionary<string, object>>())
            {
                if (!item.TryGetValue("Properties", out var p) || p is not Dictionary<string, object> properties)
                    continue;
                devices.Add(new UsbmuxDevice(
                    DeviceId: (int)GetLong(properties, "DeviceID", GetLong(item, "DeviceID", 0)),
                    SerialNumber: properties.TryGetValue("SerialNumber", out var serial) ? serial as string ?? "" : "",
                    ConnectionType: properties.TryGetValue("ConnectionType", out var type) ? type as string ?? "" : "",
                    ProductId: (int)GetLong(properties, "ProductID", 0)));
            }
        }
        return devices;
    }

    /// <summary>Opens a tunnel to TCP <paramref name="port"/> on the device. The returned stream is the tunnel.</summary>
    public static async Task<NetworkStream> ConnectAsync(int deviceId, int port, CancellationToken cancellationToken = default)
    {
        var client = await OpenAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var stream = client.GetStream();
            var request = Request("Connect");
            request["DeviceID"] = deviceId;
            // usbmuxd expects the port in network byte order, stored in an integer.
            request["PortNumber"] = (int)(ushort)IPAddress.HostToNetworkOrder((short)port);
            await SendAsync(stream, request, 2, cancellationToken).ConfigureAwait(false);

            var reply = await ReceiveAsync(stream, cancellationToken).ConfigureAwait(false);
            var result = (int)GetLong(reply, "Number", -1);
            if (result != ResultOk)
            {
                var reason = result switch
                {
                    ResultConnectionRefused => "the AnyCam app isn't listening on the iPhone (open it)",
                    ResultBadDevice => "the iPhone is no longer connected",
                    _ => $"usbmuxd error {result}",
                };
                throw new UsbmuxException($"USB connection failed: {reason}", result);
            }

            client.NoDelay = true;
            return new NetworkStream(client.Client, ownsSocket: true);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static async Task<TcpClient> OpenAsync(CancellationToken cancellationToken)
    {
        var client = new TcpClient(AddressFamily.InterNetwork);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            await client.ConnectAsync(IPAddress.Loopback, ServicePort, timeout.Token).ConfigureAwait(false);
            return client;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            client.Dispose();
            throw new UsbmuxException("Apple Mobile Device Service isn't running. Install iTunes or the Apple Devices app to use USB.");
        }
    }

    private static Dictionary<string, object> Request(string messageType) => new()
    {
        ["MessageType"] = messageType,
        ["ClientVersionString"] = "AnyCam",
        ["ProgName"] = "AnyCam",
        ["kLibUSBMuxVersion"] = 3,
    };

    private static async Task SendAsync(NetworkStream stream, Dictionary<string, object> message, int tag, CancellationToken cancellationToken)
    {
        var payload = Encoding.UTF8.GetBytes(Plist.Serialize(message));
        var packet = new byte[16 + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(packet, (uint)packet.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), PlistVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8), PlistMessage);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(12), (uint)tag);
        payload.CopyTo(packet, 16);
        await stream.WriteAsync(packet, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<Dictionary<string, object>> ReceiveAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var header = new byte[16];
        await stream.ReadExactlyAsync(header, timeout.Token).ConfigureAwait(false);
        var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (length < 16 || length > 4 * 1024 * 1024)
            throw new UsbmuxException($"Invalid usbmuxd reply length {length}");
        var payload = new byte[length - 16];
        await stream.ReadExactlyAsync(payload, timeout.Token).ConfigureAwait(false);
        return Plist.ParseDictionary(Encoding.UTF8.GetString(payload));
    }

    private static long GetLong(Dictionary<string, object> dict, string key, long fallback) =>
        dict.TryGetValue(key, out var value) && value is long number ? number : fallback;
}
