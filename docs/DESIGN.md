# AnyCam — design

AnyCam turns an iPhone into a high-quality, low-latency webcam and microphone for Windows.
It is free and open source: no subscription, no watermark, no ads, no resolution cap.

The main use is **making YouTube content**: talking-head videos, tutorials with a facecam in OBS,
desk/overhead shots and vertical Shorts. It also covers everyday calls in Teams, Google Meet, Zoom and Discord.

## 1. Architecture

```
 iPhone — AnyCam (Swift)                         Windows PC
┌──────────────────────────────┐           ┌───────────────────────────────────────────────┐
│ AVCaptureSession             │  USB      │ AnyCam Desktop (C# / .NET 10 / WPF)            │
│   lens ─► VideoToolbox       │ (usbmux)  │                                                │
│           H.264 / HEVC       │──────────►│ Connection ─► MF decoder (GPU, DXVA)           │
│   mic  ─► 48 kHz PCM         │  or Wi‑Fi │             ─► D3D11 pipeline                  │
│ Control server (JSON)        │◄──────────│                 transform · color · 3D LUT     │
│ Bonjour  _anycam._tcp        │  control  │             ─► Preview (guides, zebras, peaking)│
└──────────────────────────────┘           │             ─► Recorder (MP4, no re-encode)     │
                                           │             ─► Virtual-camera feeder            │
                                           └──────────────────────┬────────────────────────┘
                                                                  │ shared memory (NV12 / BGRA)
                                           ┌──────────────────────▼────────────────────────┐
                                           │ AnyCamVCam.dll — C++ Media Foundation source    │
                                           │ hosted by the Windows Camera Frame Server       │
                                           │ (registered with MFCreateVirtualCamera, Win 11) │
                                           └──────────────────────┬────────────────────────┘
                                                                  ▼
                                      Teams · Meet · Zoom · OBS · Discord · Camera app
```

### Key decisions

| Decision | Why |
|---|---|
| **Native iOS app** (Swift, AVFoundation) | It's the only way to get full control: lens choice, ISO, shutter, white balance in Kelvin, manual focus, stabilization, HDR, and the hardware HEVC/H.264 encoder. A browser-based sender can't do manual exposure or white balance. |
| **Phone encodes, PC decodes** | Raw 1080p60 is about 1.5 Gb/s. H.264/HEVC at 10–50 Mb/s looks clean, and both ends have hardware codecs. |
| **TCP + "drop to next keyframe" back-pressure** | The same socket works over Wi-Fi and through Apple's USB tunnel (usbmuxd). Latency stays bounded without a custom UDP stack (see PROTOCOL.md). |
| **Windows 11 virtual camera** (`MFCreateVirtualCamera`) instead of a DirectShow filter or kernel driver | It shows up as a real camera to Media Foundation apps (Camera app, Teams, Chrome/Edge) and DirectShow apps (OBS). No driver signing. The frame server loads one small C++ DLL. Downside: Windows 10 isn't supported (a DirectShow filter can be added later). |
| **Shared memory between the app and the camera DLL** | The DLL runs inside the Frame Server service (session 0, LocalService), not in the app. It creates `Global\` shared memory with an ACL that lets the user's app write frames into a triple buffer. Frames are copied once, with no extra encode. |
| **The app renders at whatever size the consumer picks** | When Teams asks for 1280×720 or OBS for 1080×1920, the GPU pipeline renders exactly that, so the DLL never scales. |
| **C# for the desktop app** | Approachable for contributors; the heavy work happens on the GPU (D3D11) and in Media Foundation. Only the virtual-camera DLL has to be C++. |
| **Recording the phone's own bitstream** | The best-quality recording costs zero re-encoding: the H.264/HEVC from the phone goes straight into the MP4. |

## 2. What creators need, and the matching features

Footage looks professional when:

1. **Exposure and color don't drift** mid-sentence → lockable/manual ISO, shutter and white balance (Kelvin + tint), all from the PC.
2. **Focus is where you want it and doesn't hunt** → focus lock, manual focus slider, click-to-focus on the preview, smooth autofocus, face-driven AF, focus peaking.
3. **No flicker under room lights** → *shutter-priority mode*: fixed shutter (1/50 or 1/100 s on 50 Hz mains like India/EU, 1/60 or 1/120 s on 60 Hz) with ISO adjusted automatically. iOS has no such mode built in; AnyCam runs its own exposure loop.
4. **Framing works for every platform** → 16:9 and native 9:16 (Shorts/Reels) streams, digital punch-in from a 4K source, 9:16 guide overlay, rule-of-thirds and safe-area guides.
5. **Exposure is judged correctly** → zebras (overexposure stripes), a histogram, and correct BT.709 color handling end to end.
6. **A consistent "look"** → color adjustments and `.cube` 3D LUTs applied on the GPU, live in the virtual camera too.
7. **Audio in sync** → the iPhone mic streams on the same clock as the video, gets muxed into recordings, and can be routed to any output device (e.g. VB-Cable to use the iPhone as a mic in other apps), with an A/V offset control.
8. **Reliability for long takes** → USB mode, auto-reconnect, battery and thermal readouts, automatic step-down when the phone gets hot, screen-dim mode, phone kept awake.
9. **Hands off the tripod** → everything above is controlled from the PC, and the phone shows a red tally border when it's live or recording.
10. **Best possible quality to edit** → passthrough recording on the PC (no re-encode); later, simultaneous 4K recording on the phone itself.

## 3. Roadmap

### v0.1 — "everything DroidCam charges for, free" (current milestone)
**Phone**
- 720p / 1080p / 4K at 24, 25, 30, 50, 60 fps (whatever the device supports); H.264 or HEVC; 2–80 Mb/s.
- Ultra-wide, wide, telephoto and front cameras.
- USB (needs Apple's "Apple Devices" app or iTunes on Windows) and Wi-Fi with Bonjour auto-discovery or manual IP.
- Remote control: zoom (with smooth ramp), focus (continuous / locked / manual / tap), exposure (auto / locked / manual / shutter-priority, EV bias), white balance (auto / locked / Kelvin+tint), torch, stabilization, HDR, smooth AF, face-driven AF, low-light boost.
- Landscape or portrait stream.
- Microphone audio.
- Tally border, battery and thermal reporting, dim mode, stays awake.
- Full-resolution photo capture (for thumbnails).

**PC**
- "AnyCam" virtual camera for every app: 1080p, 720p, 4K, 1080×1920 portrait, 30 and 60 fps.
- Preview with framing guides, zebras and focus peaking; click-to-focus.
- Mirror, flip, rotate, digital crop/zoom.
- Brightness, contrast, saturation, temperature, tint, gamma, 3D LUT (`.cube`).
- MP4 recording of the original stream plus audio (no re-encode).
- Audio output to any device, A/V offset.
- Auto-reconnect; live stats (fps, bitrate, round-trip time, decoder).

### v0.2 — creator workflow
- Presets ("Talking head", "Desk overhead", "Night") that save and restore every setting.
- "Processed" recording (with LUT and crop) through the GPU's hardware encoder (NVENC / QuickSync / AMF via Media Foundation).
- Simultaneous high-quality recording on the phone (4K HEVC, ProRes on Pro models), then transfer.
- Histogram and waveform scopes; false color.
- Global hotkeys, tray mode, start with Windows.
- Two or more phones as AnyCam 1 / AnyCam 2 (multi-cam).
- OBS source plugin (video + audio, no virtual-camera hop).

### v0.3+ — advanced
- Background blur/replace (segmentation on the GPU via ONNX Runtime + DirectML).
- Teleprompter on the phone screen when using the front camera.
- Apple Log on Pro iPhones with a monitoring LUT.
- Auto-framing (follows the subject inside a 4K frame).
- Android sender (same protocol).
- Windows 10 support via a DirectShow filter.
- Signed installer and auto-update.

## 4. Latency budget

| Stage | USB | Wi-Fi (5 GHz) |
|---|---:|---:|
| Sensor + ISP | 17–33 ms | 17–33 ms |
| Hardware encode (no B-frames, real-time) | 5–10 ms | 5–10 ms |
| Transport | 1–3 ms | 5–30 ms |
| Hardware decode (low-latency mode) | 3–8 ms | 3–8 ms |
| GPU pipeline + virtual-camera handoff | ≤ 1 frame | ≤ 1 frame |
| **Target total (before the consuming app)** | **≈ 60–90 ms** | **≈ 80–130 ms** |

## 5. Quality defaults

- 1080p30, H.264 High, 16 Mb/s over USB (10 Mb/s over Wi-Fi), 2 s GOP, no B-frames.
- For recording: 4K30 HEVC at 50 Mb/s over USB.
- BT.709, video range, tagged in the stream; the PC converts with the correct matrix.
- Locked frame rate (min = max frame duration), so recordings have even frame timing.

## 6. Risks and mitigations

| Risk | Mitigation |
|---|---|
| A free Apple ID signature expires every 7 days | AltStore / Sideloadly auto-refresh; or a paid developer account (1 year). The CI build is unsigned so anyone can sign it. |
| Some apps (notably Teams) have had quirks with MF virtual cameras | Correct media-type attributes (BT.709, nominal range), monotonic timestamps, NV12 first; test Teams/Meet/Zoom/OBS early. |
| HEVC decode needs "HEVC Video Extensions" on Windows | Detected at runtime; H.264 is the default. |
| Wi-Fi congestion | Drop-to-keyframe + adaptive bitrate; recommend 5 GHz or USB. |
| Phone heat on long 4K sessions | Thermal monitoring, automatic step-down, dim mode; recommend charging with the case off. |
| iOS stops the camera in the background | The app must stay in the foreground (dim mode makes that practical). |

## 7. Repository layout

```
docs/            design, wire protocol, setup guide
ios/             iPhone app (Swift, XcodeGen project.yml)
windows/vcam/    AnyCamVCam.dll — Media Foundation virtual camera source (C++, CMake)
windows/src/     AnyCam Desktop (WPF), AnyCam.Core, test sender / tools (C#)
.github/         CI: unsigned iOS .ipa + Windows build
```
