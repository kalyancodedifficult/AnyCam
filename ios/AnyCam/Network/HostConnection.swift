import Foundation
import Network

/// One connected PC. Reads framed packets, sends control and media packets, and applies the
/// flow control from docs/PROTOCOL.md so latency stays bounded on congested Wi-Fi.
/// All state lives on the server's network queue.
final class HostConnection {
    let connection: NWConnection
    let queue: DispatchQueue
    let id = UUID()

    /// A control message arrived (network queue).
    var onControl: ((JSON) -> Void)?
    /// The connection ended (network queue). Called once.
    var onClose: (() -> Void)?
    /// Flow control asks the encoder for an IDR frame (network queue).
    var onKeyframeNeeded: (() -> Void)?
    /// Flow control changed the encoder bitrate (network queue).
    var onBitrateChange: ((Int) -> Void)?

    private var queuedBytes = 0
    private var closed = false
    private var lastReceive = Date()
    private var livenessTimer: DispatchSourceTimer?

    // Flow control state.
    private var dropping = false
    private var targetBitrate = 16_000_000
    private var currentBitrate = 16_000_000
    private var lastDrop = Date.distantPast
    private var lastStepUp = Date.distantPast

    init(connection: NWConnection, queue: DispatchQueue) {
        self.connection = connection
        self.queue = queue
    }

    /// True when the PC reached us through Apple's USB tunnel (usbmuxd connects from the device's loopback).
    var isUSB: Bool {
        if case let .hostPort(host, _) = connection.endpoint {
            switch host {
            case let .ipv4(address):
                return address == .loopback
            case let .ipv6(address):
                return address == .loopback
            default:
                return false
            }
        }
        return false
    }

    var remoteDescription: String {
        if case let .hostPort(host, _) = connection.endpoint {
            return "\(host)"
        }
        return "\(connection.endpoint)"
    }

    func start() {
        connection.stateUpdateHandler = { [weak self] state in
            switch state {
            case .failed, .cancelled:
                self?.close()
            case .waiting:
                // An accepted connection that starts waiting has lost its path.
                self?.close()
            default:
                break
            }
        }
        connection.start(queue: queue)
        receiveHeader()

        let timer = DispatchSource.makeTimerSource(queue: queue)
        timer.schedule(deadline: .now() + 1, repeating: 1)
        timer.setEventHandler { [weak self] in
            guard let self else { return }
            if Date().timeIntervalSince(self.lastReceive) > 5 {
                self.close() // the PC pings every second; silence means it's gone
            }
        }
        timer.resume()
        livenessTimer = timer
    }

    func close() {
        queue.async { [weak self] in
            self?.closeOnQueue()
        }
    }

    private func closeOnQueue() {
        guard !closed else { return }
        closed = true
        livenessTimer?.cancel()
        livenessTimer = nil
        connection.cancel()
        onClose?()
        onClose = nil
        onControl = nil
    }

    // MARK: Receiving

    private func receiveHeader() {
        connection.receive(minimumIncompleteLength: ProtocolConstants.headerSize, maximumLength: ProtocolConstants.headerSize) { [weak self] data, _, isComplete, error in
            guard let self else { return }
            guard error == nil, let data, data.count == ProtocolConstants.headerSize, let header = PacketHeader.decode(data) else {
                if error != nil || isComplete || data == nil {
                    self.closeOnQueue()
                }
                return
            }
            self.lastReceive = Date()
            let length = Int(header.length)
            if length > ProtocolConstants.maxPayload {
                self.closeOnQueue()
            } else if length == 0 {
                self.handle(header, Data())
                self.receiveHeader()
            } else {
                self.receivePayload(header, length: length)
            }
        }
    }

    private func receivePayload(_ header: PacketHeader, length: Int) {
        connection.receive(minimumIncompleteLength: length, maximumLength: length) { [weak self] data, _, _, error in
            guard let self else { return }
            guard error == nil, let data, data.count == length else {
                self.closeOnQueue()
                return
            }
            self.lastReceive = Date()
            self.handle(header, data)
            self.receiveHeader()
        }
    }

    private func handle(_ header: PacketHeader, _ payload: Data) {
        // The host only sends control messages.
        guard header.type == PacketType.control.rawValue, let message = JSONMessage.decode(payload) else { return }
        onControl?(message)
    }

    // MARK: Sending

    func sendControl(_ message: JSON) {
        guard let payload = JSONMessage.encode(message) else { return }
        send(Packet.make(.control, payload: payload))
    }

    func sendAudio(_ pcm: Data, timestamp: Int64) {
        send(Packet.make(.audio, timestamp: timestamp, payload: pcm))
    }

    func sendPhoto(_ payload: Data) {
        send(Packet.make(.photo, payload: payload))
    }

    /// Sends an encoded access unit, dropping frames until the next keyframe when the socket backs up.
    func sendVideo(_ accessUnit: Data, keyframe: Bool, timestamp: Int64) {
        queue.async { [weak self] in
            guard let self, !self.closed else { return }
            let threshold = max(2_000_000, self.currentBitrate / 8 / 2) // ≈ 0.5 s of video
            let now = Date()

            if self.dropping {
                guard keyframe, self.queuedBytes < threshold / 2 else { return }
                self.dropping = false
            } else if self.queuedBytes > threshold {
                // Congested: skip ahead to a fresh keyframe at a lower bitrate.
                self.dropping = true
                self.lastDrop = now
                self.currentBitrate = max(1_000_000, Int(Double(self.currentBitrate) * 0.7))
                self.onBitrateChange?(self.currentBitrate)
                self.onKeyframeNeeded?()
                return
            }

            if self.currentBitrate < self.targetBitrate,
               now.timeIntervalSince(self.lastDrop) > 4,
               now.timeIntervalSince(self.lastStepUp) > 2 {
                self.lastStepUp = now
                self.currentBitrate = min(self.targetBitrate, Int(Double(self.currentBitrate) * 1.15))
                self.onBitrateChange?(self.currentBitrate)
            }

            let flags = keyframe ? (VideoFlags.keyframe | VideoFlags.parameterSets) : 0
            self.sendOnQueue(Packet.make(.video, flags: flags, timestamp: timestamp, payload: accessUnit))
        }
    }

    /// Resets flow control when the configured bitrate changes.
    func setTargetBitrate(_ bitrate: Int) {
        queue.async { [weak self] in
            guard let self else { return }
            self.targetBitrate = bitrate
            self.currentBitrate = bitrate
            self.dropping = false
        }
    }

    private func send(_ data: Data) {
        queue.async { [weak self] in
            self?.sendOnQueue(data)
        }
    }

    private func sendOnQueue(_ data: Data) {
        guard !closed else { return }
        queuedBytes += data.count
        connection.send(content: data, completion: .contentProcessed { [weak self] error in
            guard let self else { return }
            self.queuedBytes -= data.count
            if error != nil {
                self.closeOnQueue()
            }
        })
    }
}
