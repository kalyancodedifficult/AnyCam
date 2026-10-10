import Foundation
import UIKit

enum DeviceInfo {
    /// Hardware identifier such as "iPhone16,1".
    static let modelIdentifier: String = {
        var info = utsname()
        uname(&info)
        let mirror = Mirror(reflecting: info.machine)
        return mirror.children.reduce(into: "") { result, element in
            if let value = element.value as? Int8, value != 0 {
                result.append(Character(UnicodeScalar(UInt8(bitPattern: value))))
            }
        }
    }()

    /// A stable id for this installation, so the PC can remember "this phone".
    static let installationId: String = {
        let key = "anycam.installationId"
        if let existing = UserDefaults.standard.string(forKey: key) {
            return existing
        }
        let id = UUID().uuidString
        UserDefaults.standard.set(id, forKey: key)
        return id
    }()

    static let appVersion: String =
        Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "0.1.0"

    /// User-visible device name. Since iOS 16 this is the generic "iPhone" unless the app has a special
    /// entitlement; the PC also shows the model identifier.
    @MainActor
    static var displayName: String {
        let name = UIDevice.current.name
        return name.isEmpty ? "iPhone" : name
    }

    @MainActor
    static var systemVersion: String { UIDevice.current.systemVersion }
}
