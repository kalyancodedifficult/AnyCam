using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using AnyCam.Core.Protocol;

namespace AnyCam.Core.Transport;

/// <summary>A camera announced over Bonjour / mDNS.</summary>
public sealed record DiscoveredCamera(string InstanceName, string DisplayName, IPAddress Address, int Port, string? Id, string? Model)
{
    public DateTime LastSeenUtc { get; init; } = DateTime.UtcNow;

    public NetworkEndpoint ToEndpoint() => new(DisplayName, Address.ToString(), Port, Id);
}

/// <summary>
/// Minimal mDNS browser for <c>_anycam._tcp.local</c>. Sends "legacy unicast" PTR queries from an
/// ephemeral port on every IPv4 interface; responders (iOS mDNSResponder) answer directly to that
/// port, so no listener on 5353 is needed.
/// </summary>
public sealed class MdnsBrowser : IDisposable
{
    private static readonly IPEndPoint MulticastEndpoint = new(IPAddress.Parse("224.0.0.251"), 5353);
    private static readonly string ServiceName = ProtocolConstants.ServiceType + ".local";

    private readonly object _lock = new();
    private readonly Dictionary<string, DiscoveredCamera> _cameras = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<UdpClient> _sockets = [];
    private CancellationTokenSource? _cts;

    /// <summary>Raised (on a background thread) when the set of cameras changes.</summary>
    public event Action<IReadOnlyList<DiscoveredCamera>>? CamerasChanged;

    public IReadOnlyList<DiscoveredCamera> Cameras
    {
        get
        {
            lock (_lock)
                return _cameras.Values.OrderBy(c => c.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
        }
    }

    public void Start()
    {
        if (_cts is not null)
            return;
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => QueryLoopAsync(_cts.Token));
    }

    private async Task QueryLoopAsync(CancellationToken token)
    {
        var round = 0;
        while (!token.IsCancellationRequested)
        {
            try
            {
                RefreshSockets(token);
                var query = BuildQuery((ushort)Random.Shared.Next(1, ushort.MaxValue));
                List<UdpClient> sockets;
                lock (_lock)
                    sockets = [.. _sockets];
                foreach (var socket in sockets)
                {
                    try
                    {
                        await socket.SendAsync(query, MulticastEndpoint, token).ConfigureAwait(false);
                    }
                    catch (SocketException)
                    {
                        // Interface went away; it is rebuilt on the next round.
                    }
                }
                ExpireStale();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Keep browsing whatever happens.
            }

            round++;
            var delay = round < 4 ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(4);
            try
            {
                await Task.Delay(delay, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void RefreshSockets(CancellationToken token)
    {
        var addresses = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.SupportsMulticast &&
                        n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(u => u.Address)
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
            .ToHashSet();

        lock (_lock)
        {
            foreach (var socket in _sockets.ToList())
            {
                var local = ((IPEndPoint)socket.Client.LocalEndPoint!).Address;
                if (!addresses.Remove(local))
                {
                    _sockets.Remove(socket);
                    socket.Dispose();
                }
            }

            foreach (var address in addresses)
            {
                try
                {
                    var socket = new UdpClient(new IPEndPoint(address, 0));
                    socket.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, address.GetAddressBytes());
                    socket.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
                    _sockets.Add(socket);
                    _ = Task.Run(() => ReceiveLoopAsync(socket, token));
                }
                catch (SocketException)
                {
                    // Address not usable (e.g. tentative); try again next round.
                }
            }
        }
    }

    private async Task ReceiveLoopAsync(UdpClient socket, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await socket.ReceiveAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                continue; // ICMP port unreachable etc.
            }

            try
            {
                foreach (var camera in ParseResponse(result.Buffer, result.RemoteEndPoint.Address))
                    Add(camera);
            }
            catch
            {
                // Malformed packet: ignore.
            }
        }
    }

    private void Add(DiscoveredCamera camera)
    {
        bool changed;
        lock (_lock)
        {
            changed = !_cameras.TryGetValue(camera.InstanceName, out var existing) ||
                      !existing.Address.Equals(camera.Address) || existing.Port != camera.Port ||
                      existing.DisplayName != camera.DisplayName;
            _cameras[camera.InstanceName] = camera;
        }
        if (changed)
            CamerasChanged?.Invoke(Cameras);
    }

    private void ExpireStale()
    {
        bool changed = false;
        lock (_lock)
        {
            foreach (var (key, camera) in _cameras.ToList())
            {
                if (DateTime.UtcNow - camera.LastSeenUtc > TimeSpan.FromSeconds(20))
                {
                    _cameras.Remove(key);
                    changed = true;
                }
            }
        }
        if (changed)
            CamerasChanged?.Invoke(Cameras);
    }

    internal static byte[] BuildQuery(ushort id)
    {
        var packet = new List<byte>(64);
        Span<byte> header = stackalloc byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(header, id);
        BinaryPrimitives.WriteUInt16BigEndian(header[4..], 1); // QDCOUNT
        packet.AddRange(header.ToArray());
        WriteName(packet, ServiceName);
        packet.Add(0x00);
        packet.Add(12);   // QTYPE PTR
        packet.Add(0x80); // QU: ask for a unicast reply
        packet.Add(0x01); // QCLASS IN
        return [.. packet];
    }

    private static void WriteName(List<byte> packet, string name)
    {
        foreach (var label in name.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var bytes = Encoding.UTF8.GetBytes(label);
            packet.Add((byte)bytes.Length);
            packet.AddRange(bytes);
        }
        packet.Add(0);
    }

    private enum RecordType : ushort
    {
        A = 1,
        Ptr = 12,
        Txt = 16,
        Aaaa = 28,
        Srv = 33,
    }

    internal static List<DiscoveredCamera> ParseResponse(byte[] packet, IPAddress source)
    {
        var results = new List<DiscoveredCamera>();
        if (packet.Length < 12)
            return results;

        var flags = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(2));
        if ((flags & 0x8000) == 0)
            return results; // a query, not a response

        int questions = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(4));
        int records = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(6)) +
                      BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(8)) +
                      BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(10));
        var offset = 12;
        for (var i = 0; i < questions; i++)
        {
            ReadName(packet, ref offset);
            offset += 4;
        }

        var instances = new List<string>();
        var services = new Dictionary<string, (string Target, int Port)>(StringComparer.OrdinalIgnoreCase);
        var texts = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var hosts = new Dictionary<string, IPAddress>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < records && offset < packet.Length; i++)
        {
            var name = ReadName(packet, ref offset);
            if (offset + 10 > packet.Length)
                break;
            var type = (RecordType)BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(offset));
            var length = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(offset + 8));
            offset += 10;
            var dataStart = offset;
            if (dataStart + length > packet.Length)
                break;

            switch (type)
            {
                case RecordType.Ptr when name.Equals(ServiceName, StringComparison.OrdinalIgnoreCase):
                    var pointerOffset = dataStart;
                    instances.Add(ReadName(packet, ref pointerOffset));
                    break;
                case RecordType.Srv:
                    var port = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(dataStart + 4));
                    var targetOffset = dataStart + 6;
                    services[name] = (ReadName(packet, ref targetOffset), port);
                    break;
                case RecordType.Txt:
                    texts[name] = ParseTxt(packet.AsSpan(dataStart, length));
                    break;
                case RecordType.A when length == 4:
                    hosts[name] = new IPAddress(packet.AsSpan(dataStart, 4));
                    break;
            }
            offset = dataStart + length;
        }

        // A responder may send SRV/TXT without repeating the PTR.
        foreach (var name in services.Keys)
        {
            if (name.EndsWith("." + ServiceName, StringComparison.OrdinalIgnoreCase) && !instances.Contains(name, StringComparer.OrdinalIgnoreCase))
                instances.Add(name);
        }

        foreach (var instance in instances.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!services.TryGetValue(instance, out var service))
                continue;
            var address = hosts.TryGetValue(service.Target, out var hostAddress) ? hostAddress : source;
            texts.TryGetValue(instance, out var txt);
            var label = instance.EndsWith("." + ServiceName, StringComparison.OrdinalIgnoreCase)
                ? instance[..^(ServiceName.Length + 1)]
                : instance;
            var display = txt is not null && txt.TryGetValue("name", out var txtName) && txtName.Length > 0 ? txtName : label;
            results.Add(new DiscoveredCamera(instance, display, address, service.Port,
                txt?.GetValueOrDefault("id"), txt?.GetValueOrDefault("model")));
        }
        return results;
    }

    private static Dictionary<string, string> ParseTxt(ReadOnlySpan<byte> data)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var offset = 0;
        while (offset < data.Length)
        {
            int length = data[offset++];
            if (offset + length > data.Length)
                break;
            var entry = Encoding.UTF8.GetString(data.Slice(offset, length));
            offset += length;
            var equals = entry.IndexOf('=');
            if (equals > 0)
                values[entry[..equals]] = entry[(equals + 1)..];
            else if (entry.Length > 0)
                values[entry] = "";
        }
        return values;
    }

    private static string ReadName(byte[] packet, ref int offset)
    {
        var labels = new List<string>();
        var position = offset;
        var jumped = false;
        for (var guard = 0; guard < 64 && position < packet.Length; guard++)
        {
            int length = packet[position];
            if (length == 0)
            {
                position++;
                break;
            }
            if ((length & 0xC0) == 0xC0)
            {
                if (position + 1 >= packet.Length)
                    break;
                var pointer = ((length & 0x3F) << 8) | packet[position + 1];
                if (!jumped)
                    offset = position + 2;
                jumped = true;
                position = pointer;
                continue;
            }
            position++;
            if (position + length > packet.Length)
                break;
            labels.Add(Encoding.UTF8.GetString(packet, position, length));
            position += length;
        }
        if (!jumped)
            offset = position;
        return string.Join('.', labels);
    }

    public void Dispose()
    {
        _cts?.Cancel();
        lock (_lock)
        {
            foreach (var socket in _sockets)
                socket.Dispose();
            _sockets.Clear();
        }
        _cts?.Dispose();
        _cts = null;
    }
}
