import Foundation

/// What the camera should stream. The PC overrides these with `configure`; the values the user
/// picks in Settings are the defaults when the PC doesn't.
struct StreamSettings: Equatable {
    var cameraId: String?
    var width = 1920
    var height = 1080
    var fps = 30
    var codec = "h264"
    var bitrate = 16_000_000
    var orientation = "auto" // auto | landscape | portrait
    var audio = true
    var keyframeInterval = 2.0

    /// Applies the fields present in a `configure` message.
    mutating func merge(_ message: JSON) {
        if let camera = message.string("camera"), !camera.isEmpty { cameraId = camera }
        if let value = message.int("width"), value > 0 { width = value }
        if let value = message.int("height"), value > 0 { height = value }
        if let value = message.int("fps"), value > 0 { fps = min(value, 240) }
        if let value = message.string("codec"), value == "h264" || value == "hevc" { codec = value }
        if let value = message.int("bitrate"), value > 0 { bitrate = min(max(value, 500_000), 150_000_000) }
        if let value = message.string("orientation"), ["auto", "landscape", "portrait"].contains(value) { orientation = value }
        if let value = message.bool("audio") { audio = value }
        if let value = message.double("keyframeInterval"), value > 0 { keyframeInterval = min(value, 10) }
    }

    /// True when the change needs a new capture configuration (not just encoder parameters).
    func needsCaptureReconfiguration(from other: StreamSettings) -> Bool {
        cameraId != other.cameraId || width != other.width || height != other.height ||
            fps != other.fps || orientation != other.orientation || audio != other.audio
    }
}

/// Defaults the user picks on the phone, persisted in UserDefaults.
enum StoredSettings {
    private static let defaults = UserDefaults.standard

    static var stream: StreamSettings {
        get {
            var settings = StreamSettings()
            if defaults.object(forKey: "width") != nil {
                settings.width = defaults.integer(forKey: "width")
                settings.height = defaults.integer(forKey: "height")
            }
            if defaults.object(forKey: "fps") != nil { settings.fps = defaults.integer(forKey: "fps") }
            if let codec = defaults.string(forKey: "codec") { settings.codec = codec }
            if defaults.object(forKey: "bitrate") != nil { settings.bitrate = defaults.integer(forKey: "bitrate") }
            if let orientation = defaults.string(forKey: "orientation") { settings.orientation = orientation }
            if defaults.object(forKey: "audio") != nil { settings.audio = defaults.bool(forKey: "audio") }
            return settings
        }
        set {
            defaults.set(newValue.width, forKey: "width")
            defaults.set(newValue.height, forKey: "height")
            defaults.set(newValue.fps, forKey: "fps")
            defaults.set(newValue.codec, forKey: "codec")
            defaults.set(newValue.bitrate, forKey: "bitrate")
            defaults.set(newValue.orientation, forKey: "orientation")
            defaults.set(newValue.audio, forKey: "audio")
        }
    }

    static var stopWhenDisconnected: Bool {
        get { defaults.object(forKey: "stopWhenDisconnected") as? Bool ?? true }
        set { defaults.set(newValue, forKey: "stopWhenDisconnected") }
    }

    static var showPreview: Bool {
        get { defaults.object(forKey: "showPreview") as? Bool ?? true }
        set { defaults.set(newValue, forKey: "showPreview") }
    }
}
