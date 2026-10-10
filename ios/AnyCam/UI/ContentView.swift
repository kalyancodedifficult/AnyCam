import SwiftUI

struct ContentView: View {
    @EnvironmentObject private var model: AppModel
    @State private var showSettings = false

    var body: some View {
        ZStack {
            Color.black.ignoresSafeArea()

            VStack(spacing: 16) {
                header
                content
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
                footer
            }
            .padding(.horizontal, 20)
            .padding(.vertical, 12)

            if model.isDimmed {
                DimOverlay(info: model.streamInfo)
                    .onTapGesture { model.isDimmed = false }
            }

            if model.isTallyActive {
                // Tally border: the PC is using the camera (red, like a broadcast camera's tally light).
                Rectangle()
                    .strokeBorder(Color.red, lineWidth: 8)
                    .ignoresSafeArea()
                    .allowsHitTesting(false)
            }
        }
        .statusBarHidden(model.isDimmed)
        .persistentSystemOverlays(model.isDimmed ? .hidden : .automatic)
        .sheet(isPresented: $showSettings) {
            SettingsView()
                .environmentObject(model)
        }
    }

    private var header: some View {
        HStack {
            Text("AnyCam")
                .font(.title2.weight(.semibold))
            Spacer()
            StatusPill(text: model.statusText, color: statusColor)
        }
    }

    private var statusColor: Color {
        if model.isTallyActive { return .red }
        return model.status == .connected ? .green : .gray
    }

    @ViewBuilder
    private var content: some View {
        if model.isStreaming && model.showPreview {
            CameraPreviewView(session: model.camera.session, cameraID: model.activeCameraID)
                .clipShape(RoundedRectangle(cornerRadius: 16, style: .continuous))
        } else if model.isStreaming {
            VStack(spacing: 12) {
                Image(systemName: "video.fill")
                    .font(.system(size: 44))
                    .foregroundStyle(.secondary)
                Text("Streaming to your PC")
                    .font(.title3.weight(.semibold))
                Text("Preview hidden to save battery.")
                    .foregroundStyle(.secondary)
            }
        } else {
            WaitingView()
        }
    }

    private var footer: some View {
        HStack(alignment: .center, spacing: 12) {
            VStack(alignment: .leading, spacing: 4) {
                if !model.streamInfo.isEmpty {
                    Text(model.streamInfo)
                        .font(.footnote.monospacedDigit())
                }
                HStack(spacing: 10) {
                    if !model.batteryText.isEmpty {
                        Label(model.batteryText, systemImage: "battery.75")
                    }
                    if !model.thermalText.isEmpty {
                        Label(model.thermalText, systemImage: "thermometer.medium")
                            .foregroundStyle(.orange)
                    }
                }
                .font(.footnote)
                .foregroundStyle(.secondary)
            }
            Spacer()
            RoundButton(systemImage: "moon.fill", label: "Dim screen") {
                model.isDimmed = true
            }
            RoundButton(systemImage: model.showPreview ? "eye.fill" : "eye.slash.fill", label: "Preview") {
                model.showPreview.toggle()
            }
            RoundButton(systemImage: "gearshape.fill", label: "Settings") {
                showSettings = true
            }
        }
    }
}

/// Shown while no PC is streaming: how to connect.
private struct WaitingView: View {
    @EnvironmentObject private var model: AppModel

    var body: some View {
        ScrollView {
            VStack(spacing: 18) {
                Image(systemName: "web.camera")
                    .font(.system(size: 56))
                    .foregroundStyle(Color.accentColor)
                    .padding(.top, 24)
                Text("Open AnyCam on your PC")
                    .font(.title3.weight(.semibold))
                Text("It finds this iPhone automatically when both are on the same Wi‑Fi.")
                    .multilineTextAlignment(.center)
                    .foregroundStyle(.secondary)

                if !model.addresses.isEmpty {
                    VStack(spacing: 6) {
                        Text("Or connect by IP")
                            .font(.footnote)
                            .foregroundStyle(.secondary)
                        ForEach(model.addresses, id: \.self) { address in
                            Text("\(address):\(String(ProtocolConstants.port))")
                                .font(.title3.monospaced())
                                .textSelection(.enabled)
                        }
                    }
                    .padding()
                    .frame(maxWidth: .infinity)
                    .background(Color.white.opacity(0.06), in: RoundedRectangle(cornerRadius: 14, style: .continuous))
                }

                Label("USB works too: plug the iPhone into the PC (needs iTunes or the Apple Devices app on Windows).", systemImage: "cable.connector")
                    .font(.footnote)
                    .foregroundStyle(.secondary)

                if let problem = model.permissionProblem {
                    VStack(spacing: 8) {
                        Text(problem)
                            .foregroundStyle(.orange)
                        Button("Open Settings") {
                            if let url = URL(string: UIApplication.openSettingsURLString) {
                                UIApplication.shared.open(url)
                            }
                        }
                        .buttonStyle(.borderedProminent)
                    }
                }
                if let error = model.listenerError {
                    Text(error)
                        .font(.footnote)
                        .foregroundStyle(.orange)
                }
                if let event = model.lastEvent {
                    Text(event)
                        .font(.footnote)
                        .foregroundStyle(.orange)
                }
            }
            .frame(maxWidth: 520)
            .frame(maxWidth: .infinity)
        }
    }
}

private struct StatusPill: View {
    let text: String
    let color: Color

    var body: some View {
        HStack(spacing: 8) {
            Circle()
                .fill(color)
                .frame(width: 8, height: 8)
            Text(text)
                .font(.footnote.weight(.semibold))
                .lineLimit(1)
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 6)
        .background(Color.white.opacity(0.08), in: Capsule())
    }
}

private struct RoundButton: View {
    let systemImage: String
    let label: String
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            Image(systemName: systemImage)
                .font(.system(size: 17, weight: .semibold))
                .frame(width: 44, height: 44)
                .background(Color.white.opacity(0.1), in: Circle())
        }
        .buttonStyle(.plain)
        .accessibilityLabel(label)
    }
}

/// Near-black screen while streaming: saves battery and keeps the phone cool on long takes.
private struct DimOverlay: View {
    let info: String

    var body: some View {
        ZStack {
            Color.black.ignoresSafeArea()
            VStack(spacing: 6) {
                Text("AnyCam is streaming")
                Text(info.isEmpty ? "Tap to wake" : "\(info) · tap to wake")
                    .font(.footnote)
            }
            .foregroundStyle(Color.white.opacity(0.25))
        }
        .contentShape(Rectangle())
    }
}
