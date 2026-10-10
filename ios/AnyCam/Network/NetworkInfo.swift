import Darwin
import Foundation

enum NetworkInfo {
    /// IPv4 addresses a PC on the same network could connect to (Wi-Fi first, then Personal Hotspot).
    static func localAddresses() -> [String] {
        var results: [(name: String, address: String)] = []
        var list: UnsafeMutablePointer<ifaddrs>?
        guard getifaddrs(&list) == 0, let first = list else { return [] }
        defer { freeifaddrs(list) }

        var cursor: UnsafeMutablePointer<ifaddrs>? = first
        while let entry = cursor {
            let interface = entry.pointee
            cursor = interface.ifa_next

            guard let address = interface.ifa_addr, address.pointee.sa_family == UInt8(AF_INET) else { continue }
            let flags = Int32(bitPattern: interface.ifa_flags)
            guard (flags & IFF_UP) != 0, (flags & IFF_LOOPBACK) == 0 else { continue }

            let name = String(cString: interface.ifa_name)
            // en* = Wi-Fi / Ethernet adapters, bridge* = Personal Hotspot. Cellular (pdp_ip*) isn't reachable from a PC.
            guard name.hasPrefix("en") || name.hasPrefix("bridge") else { continue }

            var host = [CChar](repeating: 0, count: Int(NI_MAXHOST))
            if getnameinfo(address, socklen_t(address.pointee.sa_len), &host, socklen_t(host.count), nil, 0, NI_NUMERICHOST) == 0 {
                let text = String(cString: host)
                if !text.hasPrefix("169.254.") {
                    results.append((name, text))
                }
            }
        }

        return results
            .sorted { lhs, rhs in
                let left = lhs.name == "en0" ? 0 : (lhs.name.hasPrefix("en") ? 1 : 2)
                let right = rhs.name == "en0" ? 0 : (rhs.name.hasPrefix("en") ? 1 : 2)
                return left < right
            }
            .map(\.address)
    }
}
