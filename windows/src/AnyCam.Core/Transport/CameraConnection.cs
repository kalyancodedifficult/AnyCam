using System.Buffers;
using System.Text.Json;
using System.Text.Json.Nodes;
using AnyCam.Core.Protocol;

namespace AnyCam.Core.Transport;

public enum TransportKind
{
    WiFi,
    Usb,
    Local,
}

/// <summary>A media packet whose payload lives in a pooled buffer. Call <see cref="Release"/> when done.</summary>
public sealed class MediaPacket
{
    private byte[]? _buffer;

    internal MediaPacket(PacketHeader header, byte[] buffer)
    {
        Header = header;
        _buffer = buffer;
    }

    public PacketHeader Header { get; }
    public PacketType Type => Header.Type;
    public long Timestamp => Header.Timestamp;
    public int Length => Header.Length;
    public bool IsKeyframe => (Header.Flags & (byte)VideoFlags.Keyframe) != 0;
    public ReadOnlySpan<byte> Span => _buffer is null ? default : _buffer.AsSpan(0, Header.Length);
    public ReadOnlyMemory<byte> Memory => _buffer is null ? default : _buffer.AsMemory(0, Header.Length);

    public void Release()
    {
        var buffer = Interlocked.Exchange(ref _buffer, null);
        if (buffer is not null)
            ArrayPool<byte>.Shared.Return(buffer);
    }
}

/// <summary>
/// One framed AnyCam session over a connected stream (TCP over Wi‑Fi, or a usbmux tunnel).
/// Packets are read on a background task and raised as events on that task.
/// </summary>
public sealed class CameraConnection : IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private Task? _receiveTask;
    private int _closed;

    public CameraConnection(Stream stream, TransportKind kind, string description)
    {
        _stream = stream;
        Kind = kind;
        Description = description;
        LastReceiveUtc = DateTime.UtcNow;
    }

    public TransportKind Kind { get; }
    public string Description { get; }
    public DateTime LastReceiveUtc { get; private set; }
    public long BytesReceived => Interlocked.Read(ref _bytesReceived);
    private long _bytesReceived;

    /// <summary>A control message arrived (raised on the receive task).</summary>
    public event Action<JsonObject>? ControlReceived;

    /// <summary>A VIDEO / AUDIO / PHOTO packet arrived. The handler owns the packet and must release it.</summary>
    public event Action<MediaPacket>? MediaReceived;

    /// <summary>The connection ended (null exception = closed by us).</summary>
    public event Action<Exception?>? Closed;

    public void Start() => _receiveTask = Task.Run(ReceiveLoopAsync);

    private async Task ReceiveLoopAsync()
    {
        Exception? error = null;
        var header = new byte[ProtocolConstants.HeaderSize];
        var token = _cts.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                await _stream.ReadExactlyAsync(header, token).ConfigureAwait(false);
                var packetHeader = PacketHeader.Read(header);
                var buffer = ArrayPool<byte>.Shared.Rent(Math.Max(packetHeader.Length, 1));
                try
                {
                    if (packetHeader.Length > 0)
                        await _stream.ReadExactlyAsync(buffer.AsMemory(0, packetHeader.Length), token).ConfigureAwait(false);
                }
                catch
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                    throw;
                }

                LastReceiveUtc = DateTime.UtcNow;
                Interlocked.Add(ref _bytesReceived, ProtocolConstants.HeaderSize + packetHeader.Length);

                switch (packetHeader.Type)
                {
                    case PacketType.Control:
                        JsonObject? message = null;
                        try
                        {
                            message = JsonNode.Parse(buffer.AsSpan(0, packetHeader.Length)) as JsonObject;
                        }
                        catch (JsonException)
                        {
                            // Ignore malformed control messages rather than dropping the session.
                        }
                        finally
                        {
                            ArrayPool<byte>.Shared.Return(buffer);
                        }
                        if (message is not null)
                            ControlReceived?.Invoke(message);
                        break;

                    case PacketType.Video:
                    case PacketType.Audio:
                    case PacketType.Photo:
                        var packet = new MediaPacket(packetHeader, buffer);
                        if (MediaReceived is { } handler)
                            handler(packet);
                        else
                            packet.Release();
                        break;

                    default:
                        ArrayPool<byte>.Shared.Return(buffer); // unknown packet types are ignored
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            error = ex;
        }
        finally
        {
            CloseCore(error);
        }
    }

    public async Task SendControlAsync(JsonObject message, CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(message, ProtocolJson.Options);
        var packet = new byte[ProtocolConstants.HeaderSize + json.Length];
        new PacketHeader(PacketType.Control, 0, json.Length, 0).Write(packet);
        json.CopyTo(packet, ProtocolConstants.HeaderSize);

        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(packet, cancellationToken).ConfigureAwait(false);
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            CloseCore(ex);
            throw;
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>Fire-and-forget send; failures close the connection.</summary>
    public void Send(JsonObject message) => _ = SendSafeAsync(message);

    private async Task SendSafeAsync(JsonObject message)
    {
        try
        {
            await SendControlAsync(message).ConfigureAwait(false);
        }
        catch
        {
            // Closed is raised by SendControlAsync.
        }
    }

    private void CloseCore(Exception? error)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
            return;
        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        try
        {
            _stream.Dispose();
        }
        catch
        {
            // ignore
        }
        Closed?.Invoke(error);
    }

    public async ValueTask DisposeAsync()
    {
        CloseCore(null);
        if (_receiveTask is not null)
        {
            try
            {
                await _receiveTask.ConfigureAwait(false);
            }
            catch
            {
                // already reported through Closed
            }
        }
        _cts.Dispose();
    }
}
