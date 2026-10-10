# AnyCam for iPhone

The camera side of AnyCam: captures video (and optionally the microphone), encodes it with the hardware
H.264/HEVC encoder and streams it to AnyCam on a Windows PC over Wi-Fi or USB, following
[docs/PROTOCOL.md](../docs/PROTOCOL.md). Everything (lens, focus, exposure, white balance, zoom…) is controlled from the PC.

- iOS 17 or newer, iPhone only.
- Swift 5 / SwiftUI, AVFoundation, VideoToolbox, Network.framework. No third-party dependencies.

## Layout

| Folder | What |
|---|---|
| `App/` | `AnyCamApp` entry point, `AppModel` (reacts to the PC's commands, UI state), `StreamPipeline` (frames → encoder → network) |
| `Camera/` | `CameraController`: session, lens/format selection, orientation, controls (incl. the shutter-priority loop), capabilities/state reporting, photos |
| `Encoder/` | `VideoEncoder` (VideoToolbox, Annex-B output), `AudioConverter` (→ 48 kHz mono s16le) |
| `Network/` | `StreamServer` (NWListener + Bonjour `_anycam._tcp`), `HostConnection` (framing, flow control), local IP list |
| `Protocol/` | Packet header and JSON helpers |
| `UI/` | SwiftUI screens: status, preview, connection help, settings, dim mode, tally border |

## Building

### Without a Mac (GitHub Actions)

Every push that touches `ios/` runs [`.github/workflows/ios.yml`](../.github/workflows/ios.yml) on a macOS runner:
it generates the Xcode project with [XcodeGen](https://github.com/yonaskolb/XcodeGen), builds for devices **without code signing**
and uploads `AnyCam-unsigned.ipa` as an artifact. Sideload it with Sideloadly or AltStore and your Apple ID
(see [docs/SETUP.md](../docs/SETUP.md#2-iphone-app-sideloading-no-mac)).

### With a Mac

```sh
brew install xcodegen
cd ios
xcodegen generate
open AnyCam.xcodeproj
```

Pick your team under *Signing & Capabilities*, connect the iPhone and run.

## Notes

- iOS pauses the camera when the app goes to the background, so AnyCam keeps the screen awake while streaming.
  The **Dim** button blacks out the screen to save battery during long takes.
- The capture frame rate is locked (min = max frame duration) so recordings have even timing.
- Video is never mirrored at the source; the PC can mirror its preview or output.
