import SwiftUI

struct SettingsView: View {
    @EnvironmentObject private var model: AppModel
    @Environment(\.dismiss) private var dismiss

    private let bitrates = [4, 8, 12, 16, 25, 40, 60, 80]

    private var resolution: Binding<Int> {
        Binding(
            get: { model.defaults.height },
            set: { height in
                model.defaults.height = height
                model.defaults.width = height * 16 / 9
            })
    }

    private var bitrate: Binding<Int> {
        Binding(
            get: { model.defaults.bitrate / 1_000_000 },
            set: { model.defaults.bitrate = $0 * 1_000_000 })
    }

    var body: some View {
        NavigationStack {
            Form {
                Section {
                    Picker("Resolution", selection: resolution) {
                        Text("720p").tag(720)
                        Text("1080p").tag(1080)
                        Text("4K").tag(2160)
                    }
                    Picker("Frame rate", selection: $model.defaults.fps) {
                        ForEach([24, 25, 30, 50, 60], id: \.self) { fps in
                            Text("\(fps) fps").tag(fps)
                        }
                    }
                    Picker("Codec", selection: $model.defaults.codec) {
                        Text("H.264").tag("h264")
                        Text("HEVC").tag("hevc")
                    }
                    Picker("Bitrate", selection: bitrate) {
                        ForEach(bitrates, id: \.self) { value in
                            Text("\(value) Mb/s").tag(value)
                        }
                    }
                    Picker("Orientation", selection: $model.defaults.orientation) {
                        Text("Automatic").tag("auto")
                        Text("Landscape").tag("landscape")
                        Text("Portrait").tag("portrait")
                    }
                    Toggle("Send microphone", isOn: $model.defaults.audio)
                } header: {
                    Text("Default stream")
                } footer: {
                    Text("Used until AnyCam on the PC chooses its own settings. Under 50 Hz lighting (India, Europe) pick 25 or 50 fps to avoid flicker.")
                }

                Section {
                    Toggle("Stop the camera when the PC disconnects", isOn: $model.stopWhenDisconnected)
                    Toggle("Show the preview while streaming", isOn: $model.showPreview)
                } footer: {
                    Text("Keep AnyCam open: iOS pauses the camera when the app goes to the background. Dim the screen to save battery on long recordings.")
                }

                Section("Connect manually") {
                    if model.addresses.isEmpty {
                        Text("Join a Wi‑Fi network to see this iPhone's address.")
                            .foregroundStyle(.secondary)
                    }
                    ForEach(model.addresses, id: \.self) { address in
                        Text("\(address):\(String(ProtocolConstants.port))")
                            .font(.body.monospaced())
                            .textSelection(.enabled)
                    }
                }

                Section("About") {
                    LabeledContent("Version", value: DeviceInfo.appVersion)
                    LabeledContent("Protocol", value: "v\(ProtocolConstants.version)")
                    LabeledContent("Device", value: DeviceInfo.modelIdentifier)
                    if let url = URL(string: "https://github.com/kalyancodedifficult/AnyCam") {
                        Link("AnyCam on GitHub", destination: url)
                    }
                }
            }
            .navigationTitle("Settings")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .confirmationAction) {
                    Button("Done") { dismiss() }
                }
            }
        }
    }
}
