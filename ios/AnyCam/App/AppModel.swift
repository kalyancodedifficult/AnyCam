import AVFoundation
import SwiftUI
import UIKit

/// App state for the UI, and the brain that reacts to the PC's control messages.
@MainActor
final class AppModel: ObservableObject {
    enum Status {
        case waiting
        case connected
    }

    @Published private(set) var status: Status = .waiting
    @Published private(set) var hostName: String?
    @Published private(set) var transport = ""
    @Published private(set) var isStreaming = false
    @Published private(set) var streamInfo = ""
    @Published private(set) var tallyLive = false
    @Published private(set) var tallyRecording = false
    @Published private(set) var addresses: [String] = []
    @Published private(set) var batteryText = ""
    @Published private(set) var thermalText = ""
    @Published private(set) var listenerError: String?
    @Published private(set) var permissionProblem: String?
    @Published private(set) var lastEvent: String?
    @Published private(set) var activeCameraID: String?

    @Published var isDimmed = false {
        didSet { applyDim() }
    }

    @Published var showPreview = StoredSettings.showPreview {
        didSet { StoredSettings.showPreview = showPreview }
    }

    @Published var defaults = StoredSettings.stream {
        didSet {
            StoredSettings.stream = defaults
            if !isStreaming {
                streamSettings = defaults // used until the PC sends its own `configure`
            }
        }
    }

    @Published var stopWhenDisconnected = StoredSettings.stopWhenDisconnected {
        didSet { StoredSettings.stopWhenDisconnected = stopWhenDisconnected }
    }

    let camera = CameraController()
    private let pipeline = StreamPipeline()
    private let server: StreamServer
    private var connection: HostConnection?
    private var streamSettings = StoredSettings.stream
    private var started = false
    private var stopWork: DispatchWorkItem?
    private var stateTimer: Timer?
    private var stateScheduled = false
    private var savedBrightness: CGFloat?
    private var thermalThrottled = false

    init() {
        server = StreamServer(serviceName: Self.serviceName, txt: [
            "v": "\(ProtocolConstants.version)",
            "id": DeviceInfo.installationId,
            "name": DeviceInfo.displayName,
            "model": DeviceInfo.modelIdentifier,
            "app": DeviceInfo.appVersion,
        ])
        camera.delegate = pipeline
    }

    /// Bonjour instance name: unique-ish so two iPhones on one network don't clash.
    private static var serviceName: String {
        let suffix = DeviceInfo.installationId.prefix(4)
        return "\(DeviceInfo.displayName) (\(suffix))"
    }

    var isTallyActive: Bool { status == .connected && (tallyLive || tallyRecording) }

    var statusText: String {
        switch status {
        case .waiting:
            return "Waiting for PC"
        case .connected:
            if tallyRecording { return "REC" }
            if tallyLive { return "LIVE" }
            let host = hostName.map { " · \($0)" } ?? ""
            return "Connected · \(transport)\(host)"
        }
    }

    // MARK: Lifecycle

    func start() {
        guard !started else { return }
        started = true

        UIDevice.current.isBatteryMonitoringEnabled = true
        NotificationCenter.default.addObserver(forName: UIDevice.batteryLevelDidChangeNotification, object: nil, queue: .main) { [weak self] _ in
            Task { @MainActor in self?.updateDeviceStatus() }
        }
        NotificationCenter.default.addObserver(forName: UIDevice.batteryStateDidChangeNotification, object: nil, queue: .main) { [weak self] _ in
            Task { @MainActor in self?.updateDeviceStatus() }
        }
        NotificationCenter.default.addObserver(forName: ProcessInfo.thermalStateDidChangeNotification, object: nil, queue: .main) { [weak self] _ in
            Task { @MainActor in self?.thermalStateChanged() }
        }
        updateDeviceStatus()

        pipeline.onStateChange = { [weak self] in
            Task { @MainActor in self?.scheduleState() }
        }
        pipeline.onEvent = { [weak self] level, code, message in
            Task { @MainActor in self?.sendEvent(level: level, code: code, message: message) }
        }
        pipeline.onVideoFormat = { [weak self] codec, width, height in
            Task { @MainActor in self?.updateStreamInfo(codec: codec, width: width, height: height) }
        }

        server.onConnect = { [weak self] host in
            // Runs on the network queue before any packet is read, so no message is missed.
            host.onControl = { [weak self, weak host] message in
                guard let host else { return }
                Task { @MainActor in self?.handle(message, from: host) }
            }
            Task { @MainActor in self?.hostConnected(host) }
        }
        server.onDisconnect = { [weak self] host in
            Task { @MainActor in self?.hostDisconnected(host) }
        }
        server.onListenerState = { [weak self] listening, error in
            Task { @MainActor in self?.listenerError = listening ? nil : error }
        }
        server.start()

        refreshAddresses()
        requestPermissions()

        stateTimer = Timer.scheduledTimer(withTimeInterval: 2, repeats: true) { [weak self] _ in
            Task { @MainActor in
                self?.sendState()
                self?.refreshAddresses()
            }
        }
    }

    func scenePhaseChanged(_ phase: ScenePhase) {
        switch phase {
        case .active:
            UIApplication.shared.isIdleTimerDisabled = true
            server.ensureRunning()
            refreshAddresses()
            updateDeviceStatus()
            if isStreaming {
                camera.start(with: streamSettings) // iOS stops the camera in the background
            }
        case .background:
            UIApplication.shared.isIdleTimerDisabled = false
        default:
            break
        }
    }

    private func requestPermissions() {
        AVCaptureDevice.requestAccess(for: .video) { [weak self] granted in
            Task { @MainActor in
                if !granted {
                    self?.permissionProblem = "Camera access is off. Turn it on in Settings › AnyCam."
                }
            }
            AVCaptureDevice.requestAccess(for: .audio) { _ in }
        }
    }

    // MARK: Connection

    private func hostConnected(_ host: HostConnection) {
        stopWork?.cancel()
        stopWork = nil
        connection = host
        status = .connected
        transport = host.isUSB ? "USB" : "Wi-Fi"
        hostName = nil
        tallyLive = false
        tallyRecording = false

        host.sendControl([
            "t": "hello",
            "proto": ProtocolConstants.version,
            "role": "camera",
            "app": "AnyCam",
            "version": DeviceInfo.appVersion,
            "platform": "ios",
            "os": DeviceInfo.systemVersion,
            "model": DeviceInfo.modelIdentifier,
            "name": DeviceInfo.displayName,
            "id": DeviceInfo.installationId,
        ])
        camera.sessionQueue.async { [camera] in
            let caps = camera.capabilities()
            host.sendControl(caps)
        }
        sendState()

        if isStreaming {
            pipeline.attach(host, settings: streamSettings)
        }
    }

    private func hostDisconnected(_ host: HostConnection) {
        guard connection === host else { return }
        connection = nil
        status = .waiting
        hostName = nil
        tallyLive = false
        tallyRecording = false
        pipeline.attach(nil, settings: streamSettings)

        // Keep the camera warm briefly so a quick reconnect (Wi-Fi blip, cable wiggle) is instant.
        guard isStreaming, stopWhenDisconnected else { return }
        let work = DispatchWorkItem { [weak self] in
            Task { @MainActor in
                guard let self, self.connection == nil else { return }
                self.stopStreaming()
            }
        }
        stopWork = work
        DispatchQueue.main.asyncAfter(deadline: .now() + 3, execute: work)
    }

    private func handle(_ message: JSON, from host: HostConnection) {
        guard host === connection else { return }
        switch message.messageType {
        case "hello":
            hostName = message.string("name")
            if let proto = message.int("proto"), proto != ProtocolConstants.version {
                sendEvent(level: "error", code: "protocol", message: "Protocol v\(proto) isn't supported by this app (v\(ProtocolConstants.version)). Update both apps.")
            }
        case "configure":
            let previous = streamSettings
            streamSettings.merge(message)
            if isStreaming {
                if streamSettings.needsCaptureReconfiguration(from: previous) {
                    camera.reconfigure(streamSettings)
                }
                pipeline.update(settings: streamSettings)
            }
            scheduleState()
        case "start":
            startStreaming()
        case "stop":
            stopStreaming()
        case "set":
            camera.apply(message)
        case "focus":
            camera.focus(x: message.double("x") ?? 0.5, y: message.double("y") ?? 0.5,
                         focus: message.bool("focus") ?? true, expose: message.bool("expose") ?? true)
        case "keyframe":
            pipeline.requestKeyframe()
        case "tally":
            tallyLive = message.bool("live") ?? false
            tallyRecording = message.bool("recording") ?? false
        case "photo":
            capturePhoto(id: message.int("id") ?? 0, format: message.string("format") ?? "jpeg", for: host)
        case "ping":
            var pong = message
            pong["t"] = "pong"
            host.sendControl(pong)
        case "ui":
            if let dim = message.bool("dim") {
                isDimmed = dim
            }
        default:
            break // unknown messages are ignored (forward compatibility)
        }
    }

    // MARK: Streaming

    private func startStreaming() {
        guard permissionProblem == nil else {
            sendEvent(level: "error", code: "unsupported", message: "Camera access is turned off on the iPhone.")
            return
        }
        stopWork?.cancel()
        stopWork = nil
        if thermalThrottled {
            streamSettings.bitrate = max(1_000_000, streamSettings.bitrate / 2)
        }
        pipeline.attach(connection, settings: streamSettings)
        camera.start(with: streamSettings)
        isStreaming = true
        UIApplication.shared.isIdleTimerDisabled = true
        scheduleState()
    }

    private func stopStreaming() {
        camera.stop()
        isStreaming = false
        streamInfo = ""
        scheduleState()
    }

    private func updateStreamInfo(codec: String, width: Int, height: Int) {
        let name = codec == "hevc" ? "HEVC" : "H.264"
        streamInfo = "\(width)×\(height) · \(streamSettings.fps) fps · \(name) · \(streamSettings.bitrate / 1_000_000) Mb/s"
    }

    private func capturePhoto(id: Int, format: String, for host: HostConnection) {
        camera.capturePhoto(format: format) { data, width, height, actualFormat in
            guard let data else {
                host.sendControl(["t": "event", "level": "warning", "code": "photo", "message": "The photo couldn't be taken."])
                return
            }
            let info: JSON = ["id": id, "format": actualFormat, "width": width, "height": height]
            guard let json = JSONMessage.encode(info) else { return }
            var payload = Data()
            let length = UInt32(json.count)
            payload.append(contentsOf: [UInt8(length & 0xFF), UInt8((length >> 8) & 0xFF), UInt8((length >> 16) & 0xFF), UInt8((length >> 24) & 0xFF)])
            payload.append(json)
            payload.append(data)
            host.sendPhoto(payload)
        }
    }

    // MARK: State reporting

    /// Coalesces bursts of changes (e.g. a zoom ramp) into one `state` message.
    private func scheduleState() {
        guard !stateScheduled else { return }
        stateScheduled = true
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.15) { [weak self] in
            Task { @MainActor in
                self?.stateScheduled = false
                self?.sendState()
            }
        }
    }

    private func sendState() {
        guard let connection else { return }
        let settings = streamSettings
        let streaming = isStreaming
        let battery = UIDevice.current.batteryLevel
        let charging = UIDevice.current.batteryState == .charging || UIDevice.current.batteryState == .full
        let thermal = Self.thermalName(ProcessInfo.processInfo.thermalState)
        camera.sessionQueue.async { [camera] in
            var state = camera.stateFields()
            state["t"] = "state"
            state["streaming"] = streaming && ((state["streaming"] as? Bool) ?? false)
            state["codec"] = settings.codec
            state["bitrate"] = settings.bitrate
            if battery >= 0 {
                state["battery"] = Double(battery)
            }
            state["charging"] = charging
            state["thermal"] = thermal
            connection.sendControl(state)
            let cameraID = state["camera"] as? String
            Task { @MainActor [weak self] in
                if self?.activeCameraID != cameraID {
                    self?.activeCameraID = cameraID
                }
            }
        }
    }

    private func sendEvent(level: String, code: String, message: String) {
        if level != "info" {
            lastEvent = message
        }
        connection?.sendControl(["t": "event", "level": level, "code": code, "message": message])
    }

    // MARK: Device status

    private func updateDeviceStatus() {
        let level = UIDevice.current.batteryLevel
        let charging = UIDevice.current.batteryState == .charging || UIDevice.current.batteryState == .full
        batteryText = level < 0 ? "" : "\(charging ? "⚡︎ " : "")\(Int((level * 100).rounded()))%"
        thermalText = Self.thermalLabel(ProcessInfo.processInfo.thermalState)
    }

    private func thermalStateChanged() {
        updateDeviceStatus()
        let state = ProcessInfo.processInfo.thermalState
        switch state {
        case .serious, .critical:
            if !thermalThrottled {
                thermalThrottled = true
                pipeline.setBitrate(max(1_000_000, streamSettings.bitrate / 2))
                sendEvent(level: "warning", code: "thermal", message: "The iPhone is hot: lowering the bitrate. Take the case off and keep it out of the sun.")
            }
            if state == .critical, streamSettings.fps > 30, isStreaming {
                streamSettings.fps = 30
                camera.reconfigure(streamSettings)
                pipeline.update(settings: streamSettings)
                sendEvent(level: "warning", code: "thermal", message: "The iPhone is very hot: dropping to 30 fps.")
            }
        case .nominal, .fair:
            if thermalThrottled {
                thermalThrottled = false
                pipeline.setBitrate(streamSettings.bitrate)
            }
        @unknown default:
            break
        }
        scheduleState()
    }

    private static func thermalName(_ state: ProcessInfo.ThermalState) -> String {
        switch state {
        case .nominal: return "nominal"
        case .fair: return "fair"
        case .serious: return "serious"
        case .critical: return "critical"
        @unknown default: return "nominal"
        }
    }

    private static func thermalLabel(_ state: ProcessInfo.ThermalState) -> String {
        switch state {
        case .serious: return "Hot"
        case .critical: return "Too hot"
        case .fair: return "Warm"
        default: return ""
        }
    }

    private func refreshAddresses() {
        let current = NetworkInfo.localAddresses()
        if current != addresses {
            addresses = current
        }
    }

    // MARK: Dim mode

    private func applyDim() {
        guard let screen = UIApplication.shared.connectedScenes.compactMap({ $0 as? UIWindowScene }).first?.screen else { return }
        if isDimmed {
            if savedBrightness == nil {
                savedBrightness = screen.brightness
            }
            screen.brightness = 0
        } else if let savedBrightness {
            screen.brightness = savedBrightness
            self.savedBrightness = nil
        }
    }
}
