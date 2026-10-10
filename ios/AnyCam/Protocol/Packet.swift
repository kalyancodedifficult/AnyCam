import Foundation

/// Constants of the AnyCam wire protocol (see docs/PROTOCOL.md).
enum ProtocolConstants {
    static let version = 1
    static let port: UInt16 = 47200
    static let serviceType = "_anycam._tcp"
    static let headerSize = 16
    static let maxPayload = 64 * 1024 * 1024
}

enum PacketType: UInt8 {
    case control = 0x01
    case video = 0x10
    case audio = 0x20
    case photo = 0x30
}

enum VideoFlags {
    static let keyframe: UInt8 = 0x01
    static let parameterSets: UInt8 = 0x02
}

/// The 16-byte little-endian header in front of every payload.
struct PacketHeader {
    var type: UInt8
    var flags: UInt8
    var length: UInt32
    var timestamp: Int64

    func encoded() -> Data {
        var bytes = [UInt8](repeating: 0, count: ProtocolConstants.headerSize)
        bytes[0] = type
        bytes[1] = flags
        for i in 0..<4 {
            bytes[4 + i] = UInt8(truncatingIfNeeded: length >> (8 * UInt32(i)))
        }
        let time = UInt64(bitPattern: timestamp)
        for i in 0..<8 {
            bytes[8 + i] = UInt8(truncatingIfNeeded: time >> (8 * UInt64(i)))
        }
        return Data(bytes)
    }

    static func decode(_ data: Data) -> PacketHeader? {
        guard data.count >= ProtocolConstants.headerSize else { return nil }
        let bytes = [UInt8](data.prefix(ProtocolConstants.headerSize))
        var length: UInt32 = 0
        for i in 0..<4 {
            length |= UInt32(bytes[4 + i]) << (8 * UInt32(i))
        }
        var time: UInt64 = 0
        for i in 0..<8 {
            time |= UInt64(bytes[8 + i]) << (8 * UInt64(i))
        }
        return PacketHeader(type: bytes[0], flags: bytes[1], length: length, timestamp: Int64(bitPattern: time))
    }
}

enum Packet {
    static func make(_ type: PacketType, flags: UInt8 = 0, timestamp: Int64 = 0, payload: Data) -> Data {
        var data = PacketHeader(type: type.rawValue, flags: flags, length: UInt32(payload.count), timestamp: timestamp).encoded()
        data.append(payload)
        return data
    }
}
