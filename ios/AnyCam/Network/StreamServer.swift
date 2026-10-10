import Foundation
import Network

/// TCP server the PC connects to (directly over Wi-Fi, or through Apple's USB tunnel), advertised
/// with Bonjour as `_anycam._tcp`. Only one PC is active at a time: a new connection replaces the old one.
final class StreamServer {
    let queue = DispatchQueue(label: "anycam.network", qos: .userInitiated)

    /// Called on the network queue.
    var onConnect: ((HostConnection) -> Void)?
    var onDisconnect: ((HostConnection) -> Void)?
    var onListenerState: ((Bool, String?) -> Void)?

    private var listener: NWListener?
    private var current: HostConnection?
    private var serviceName: String
    private var txt: [String: String]
    private var restartScheduled = false

    init(serviceName: String, txt: [String: String]) {
        self.serviceName = serviceName
        self.txt = txt
    }

    var activeConnection: HostConnection? {
        queue.sync { current }
    }

    func start() {
        queue.async { [weak self] in
            self?.startListener()
        }
    }

    /// Re-creates the listener if iOS tore it down (e.g. after the app was suspended).
    func ensureRunning() {
        queue.async { [weak self] in
            guard let self else { return }
            if self.listener == nil || self.listener?.state != .ready {
                self.listener?.cancel()
                self.listener = nil
                self.startListener()
            }
        }
    }

    func stop() {
        queue.async { [weak self] in
            guard let self else { return }
            self.listener?.cancel()
            self.listener = nil
            self.current?.close()
            self.current = nil
        }
    }

    private func startListener() {
        guard listener == nil, let port = NWEndpoint.Port(rawValue: ProtocolConstants.port) else { return }

        let tcp = NWProtocolTCP.Options()
        tcp.noDelay = true
        let parameters = NWParameters(tls: nil, tcp: tcp)
        parameters.allowLocalEndpointReuse = true

        do {
            let listener = try NWListener(using: parameters, on: port)
            let record = NWTXTRecord(txt)
            listener.service = NWListener.Service(name: serviceName, type: ProtocolConstants.serviceType, domain: nil, txtRecord: record.data)
            listener.stateUpdateHandler = { [weak self, weak listener] state in
                guard let self, let listener else { return }
                self.handleState(state, of: listener)
            }
            listener.newConnectionHandler = { [weak self] connection in
                self?.accept(connection)
            }
            listener.start(queue: queue)
            self.listener = listener
        } catch {
            onListenerState?(false, "Couldn't open port \(ProtocolConstants.port): \(error.localizedDescription)")
            scheduleRestart()
        }
    }

    private func handleState(_ state: NWListener.State, of listener: NWListener) {
        switch state {
        case .ready:
            onListenerState?(true, nil)
        case let .failed(error):
            onListenerState?(false, error.localizedDescription)
            listener.cancel()
            if self.listener === listener {
                self.listener = nil
            }
            scheduleRestart()
        case let .waiting(error):
            onListenerState?(false, error.localizedDescription)
        case .cancelled:
            if self.listener === listener {
                self.listener = nil
            }
        default:
            break
        }
    }

    private func scheduleRestart() {
        guard !restartScheduled else { return }
        restartScheduled = true
        queue.asyncAfter(deadline: .now() + 2) { [weak self] in
            guard let self else { return }
            self.restartScheduled = false
            self.startListener()
        }
    }

    private func accept(_ connection: NWConnection) {
        let host = HostConnection(connection: connection, queue: queue)
        let previous = current
        current = host
        host.onClose = { [weak self, weak host] in
            guard let self, let host else { return }
            if self.current === host {
                self.current = nil
            }
            self.onDisconnect?(host)
        }
        previous?.close()
        host.start()
        onConnect?(host)
    }
}
