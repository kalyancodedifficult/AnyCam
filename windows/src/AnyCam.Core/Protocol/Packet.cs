using System.Buffers.Binary;

namespace AnyCam.Core.Protocol;

/// <summary>Constants of the AnyCam wire protocol (see docs/PROTOCOL.md).</summary>
public static class ProtocolConstants
{
    public const int Version = 1;
    public const int DefaultPort = 47200;
    public const string ServiceType = "_anycam._tcp";
    public const int HeaderSize = 16;
    public const int MaxPayloadSize = 64 * 1024 * 1024;
}

public enum PacketType : byte
{
    Control = 0x01,
    Video = 0x10,
    Audio = 0x20,
    Photo = 0x30,
}

[Flags]
public enum VideoFlags : byte
{
    None = 0,
    Keyframe = 0x01,
    ParameterSets = 0x02,
}

/// <summary>The 16-byte little-endian header that precedes every payload.</summary>
public readonly record struct PacketHeader(PacketType Type, byte Flags, int Length, long Timestamp)
{
    public static PacketHeader Read(ReadOnlySpan<byte> source)
    {
        if (source.Length < ProtocolConstants.HeaderSize)
            throw new ArgumentException("Header needs 16 bytes", nameof(source));

        var type = (PacketType)source[0];
        var flags = source[1];
        var length = BinaryPrimitives.ReadUInt32LittleEndian(source[4..]);
        var timestamp = BinaryPrimitives.ReadInt64LittleEndian(source[8..]);
        if (length > ProtocolConstants.MaxPayloadSize)
            throw new InvalidDataException($"Payload of {length} bytes exceeds the protocol limit");
        return new PacketHeader(type, flags, (int)length, timestamp);
    }

    public void Write(Span<byte> destination)
    {
        if (destination.Length < ProtocolConstants.HeaderSize)
            throw new ArgumentException("Header needs 16 bytes", nameof(destination));

        destination[0] = (byte)Type;
        destination[1] = Flags;
        destination[2] = 0;
        destination[3] = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(destination[4..], (uint)Length);
        BinaryPrimitives.WriteInt64LittleEndian(destination[8..], Timestamp);
    }
}
