<p align="center"><img src="docs/assets/icon-256.png" width="96" alt="AnyCam icon"></p>

<h1 align="center">AnyCam</h1>

<p align="center"><b>Use your iPhone as a high-quality webcam on Windows: free, open source, no watermark, no subscription.</b><br>
Made for creators: manual exposure, white balance and focus, every lens, 4K, original-quality recording, and a virtual camera that works in Teams, Google Meet, Zoom, Discord and OBS.</p>

---

## Why

DroidCam-style apps lock the features creators need (HD, manual controls, no watermark) behind a subscription.
AnyCam does all of it for free, and adds what a YouTube setup actually needs:

| | AnyCam |
|---|---|
| Resolution | 720p, 1080p, 4K at 24/25/30/50/60 fps (whatever your iPhone supports) |
| Lenses | Ultra-wide 0.5×, wide 1×, telephoto 2×/3×/5×, front |
| Connection | USB (lowest latency) or Wi-Fi with automatic discovery |
| Camera control from the PC | Zoom, focus (auto/lock/manual, click-to-focus), exposure (auto/lock/manual, **shutter priority for flicker-free 50 Hz lighting**), EV, white balance in Kelvin, torch, stabilization, HDR |
| Picture | Mirror/rotate, 4K→1080p punch-in, color adjustments, `.cube` 3D LUTs, vertical 9:16 output for Shorts |
| Monitoring | Zebras, focus peaking, rule-of-thirds grid, safe areas, 9:16 guide |
| Recording | The phone's original H.264/HEVC + audio straight to MP4 (no re-encode), full-res photos for thumbnails |
| Virtual camera | Native Windows 11 camera "AnyCam": 1080p / 720p / 4K / 1080×1920 vertical |
| Audio | iPhone mic, in sync, to any output (use VB-Cable for a virtual microphone) |
| Reliability | Auto-reconnect, tally light on the phone, battery and heat monitoring, dim mode |

The full design rationale and roadmap are in [docs/DESIGN.md](docs/DESIGN.md).

## Getting started

👉 **Step-by-step guide: [docs/SETUP.md](docs/SETUP.md)**

1. **Windows 11 PC**: download `AnyCam-Windows-x64.zip` from the latest build ([Actions](https://github.com/kalyancodedifficult/AnyCam/actions/workflows/windows.yml) or Releases), extract it, run `AnyCam.exe` and click **Install virtual camera** on the Output tab (one admin prompt).
2. **iPhone (iOS 17+)**: download `AnyCam-unsigned.ipa` ([Actions](https://github.com/kalyancodedifficult/AnyCam/actions/workflows/ios.yml) or Releases) and sideload it with [Sideloadly](https://sideloadly.io) or [AltStore](https://altstore.io) using your Apple ID. No Mac needed.
3. Open AnyCam on both. On the same Wi-Fi the PC finds the phone automatically; or plug in USB.
4. In Teams / Meet / Zoom / OBS, pick the camera named **AnyCam**.

## How it works

```
iPhone (Swift)                          Windows (C# + C++)
camera → VideoToolbox H.264/HEVC ──USB/Wi‑Fi──▶ GPU decode → D3D11 pipeline → preview
mic → 48 kHz PCM                     (TCP)      (color, LUT, framing)      → MP4 recorder
control server  ◀── JSON commands ──            → shared memory → AnyCamVCam.dll
                                                   (Windows 11 virtual camera, runs in the Frame Server)
```

- Wire protocol: [docs/PROTOCOL.md](docs/PROTOCOL.md). It's small, so other senders (Android…) can implement it.
- `ios/`: iPhone app (SwiftUI, AVFoundation, VideoToolbox, Network.framework).
- `windows/src/AnyCam.Desktop`: the PC app (WPF, .NET 10).
- `windows/src/AnyCam.Core`: protocol, USB (usbmuxd) and Wi‑Fi transport, Media Foundation decoding, D3D11 processing, recorder.
- `windows/vcam`: the virtual camera: a Media Foundation media source registered with `MFCreateVirtualCamera`.
- `windows/src/AnyCam.Emulator`: a fake phone (test pattern + beep), so the PC side can be developed without an iPhone.

## Building from source

**Windows** (Visual Studio 2022/2026 with *Desktop development with C++*, .NET 10 SDK):

```powershell
# virtual camera (C++)
cmake -S windows/vcam -B windows/vcam/build -G Ninja -DCMAKE_BUILD_TYPE=RelWithDebInfo   # from a VS developer prompt
cmake --build windows/vcam/build
powershell -ExecutionPolicy Bypass -File windows/scripts/dev-install.ps1                  # registers it (one UAC prompt)

# desktop app + tests
dotnet build windows/src/AnyCam.slnx
dotnet test windows/src/AnyCam.Tests
dotnet run --project windows/src/AnyCam.Emulator     # optional: fake phone on 127.0.0.1
dotnet run --project windows/src/AnyCam.Desktop
```

**iPhone**: CI builds an unsigned IPA on every push (see [ios/README.md](ios/README.md)). On a Mac: `brew install xcodegen && cd ios && xcodegen generate && open AnyCam.xcodeproj`.

## Status

v0.1, early but working end to end. Verified on Windows 11 with the emulator: hardware decode, virtual camera at a steady 30 fps
in a Media Foundation client and in Microsoft Edge (`getUserMedia`, the same path Google Meet and the new Teams use), recording, photos, overlays.
The iPhone app builds in CI (Xcode 26) and needs real-device testing; please open issues with your iPhone model and what you see.

## Acknowledgements

- [smourier/VCamSample](https://github.com/smourier/VCamSample) (MIT) and Microsoft's [Windows-Camera VirtualCamera sample](https://github.com/microsoft/Windows-Camera/tree/master/Samples/VirtualCamera) (MIT) were the references for the Media Foundation virtual camera.
- [Vortice.Windows](https://github.com/amerkoleci/Vortice.Windows) (MIT) for Direct3D/Media Foundation bindings, [NAudio](https://github.com/naudio/NAudio) (MIT) for audio output.

## License

[MIT](LICENSE)
