import CoreGraphics
import Foundation

/// Control messages are JSON objects; `[String: Any]` keeps partial updates (`set`, `configure`) simple.
typealias JSON = [String: Any]

enum JSONMessage {
    static func encode(_ object: JSON) -> Data? {
        let clean = sanitize(object)
        guard JSONSerialization.isValidJSONObject(clean) else { return nil }
        return try? JSONSerialization.data(withJSONObject: clean, options: [])
    }

    static func decode(_ data: Data) -> JSON? {
        (try? JSONSerialization.jsonObject(with: data, options: [])) as? JSON
    }

    /// JSON can't carry NaN or infinity (e.g. an indefinite exposure duration): replace them with 0.
    private static func sanitize(_ object: JSON) -> JSON {
        var result = JSON()
        for (key, value) in object {
            result[key] = sanitizeValue(value)
        }
        return result
    }

    private static func sanitizeValue(_ value: Any) -> Any {
        switch value {
        case let number as Double:
            return number.isFinite ? number : 0
        case let number as Float:
            return number.isFinite ? Double(number) : 0
        case let number as CGFloat:
            return number.isFinite ? Double(number) : 0
        case let dictionary as JSON:
            return sanitize(dictionary)
        case let array as [Any]:
            return array.map(sanitizeValue)
        default:
            return value
        }
    }
}

extension Dictionary where Key == String, Value == Any {
    var messageType: String? { self["t"] as? String }

    func string(_ key: String) -> String? { self[key] as? String }

    func double(_ key: String) -> Double? {
        if let number = self[key] as? NSNumber { return number.doubleValue }
        if let text = self[key] as? String { return Double(text) }
        return nil
    }

    func int(_ key: String) -> Int? {
        guard let value = double(key), value.isFinite else { return nil }
        return Int(value.rounded())
    }

    func bool(_ key: String) -> Bool? {
        if let flag = self[key] as? Bool { return flag }
        if let number = self[key] as? NSNumber { return number.boolValue }
        return nil
    }
}
