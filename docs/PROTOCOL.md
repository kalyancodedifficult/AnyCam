# AnyCam wire protocol — version 1

This is the contract between a **camera** (the phone app) and a **host** (the PC app).
It is deliberately small so that other senders (Android, a test emulator, …) can implement it.

## Transport

| | |
|---|---|
| Socket | TCP. The **camera is the server**, the host connects. |
| Port | `47200` (default) |
| Discovery | Bonjour / mDNS service type `_anycam._tcp` (TXT keys below) |
| USB (iPhone) | The host tunnels the same TCP port through Apple's `usbmuxd` (Apple Mobile Device Service on Windows, `127.0.0.1:27015`). Nothing changes on the camera side. |
| Connections | One active host at a time. A new connection replaces the previous one. |
| Byte order | Little-endian for every integer in headers. |

Bonjour TXT record keys: `v` (protocol version, `1`), `id` (stable camera id), `name` (user-visible device name), `model` (e.g. `iPhone16,1`), `app` (app version).

The camera should set `TCP_NODELAY`.

## Framing

Every message is a 16-byte header followed by `length` bytes of payload.

| Offset | Size | Field | Notes |
|---:|---:|---|---|
| 0 | 1 | `type` | packet type (table below) |
| 1 | 1 | `flags` | type-specific bit flags |
| 2 | 2 | `reserved` | must be 0 |
| 4 | 4 | `length` | payload length in bytes (u32) |
| 8 | 8 | `timestamp` | i64, microseconds on the sender's monotonic clock (media packets); 0 otherwise |

Receivers must reject a payload larger than 64 MiB and close the connection.

### Packet types

| Type | Name | Direction | Payload |
|---:|---|---|---|
| `0x01` | `CONTROL` | both | UTF-8 JSON object with a `"t"` (message type) field |
| `0x10` | `VIDEO` | camera → host | One encoded access unit, **Annex-B** (start codes) |
| `0x20` | `AUDIO` | camera → host | PCM, signed 16-bit little-endian, interleaved |
| `0x30` | `PHOTO` | camera → host | `u32 jsonLength` + JSON metadata + image bytes |

`VIDEO` flags: `0x01` = keyframe (IDR / IRAP), `0x02` = contains parameter sets (SPS/PPS, plus VPS for HEVC).
Every keyframe **must** be preceded (in the same packet) by its parameter sets, so a host can start decoding at any keyframe.
The codec, size and frame rate of the bitstream are announced by the `video` control message.

`AUDIO` format is announced by the `audio` control message. Audio and video timestamps use the **same clock**, so hosts can mux them without drift.

`PHOTO` metadata JSON: `{"id": 3, "format": "jpeg" | "heic", "width": 4032, "height": 3024}`.

## Session flow

```
host                                camera
 |------------- TCP connect -------->|
 |<------------ hello ---------------|   (both sides send hello immediately)
 |------------- hello -------------->|
 |<------------ caps ----------------|
 |<------------ state ---------------|
 |------------- configure ---------->|   (optional: camera, size, fps, codec, bitrate…)
 |------------- start -------------->|
 |<------------ video, audio --------|   (formats)
 |<------------ VIDEO / AUDIO -------|   (media packets…)
 |------------- ping (1 Hz) -------->|
 |<------------ pong ----------------|
 |------------- set / focus / …  --->|
 |<------------ state (on change, ≥ every 2 s while streaming)
```

* If the major protocol version differs, the side that notices sends an `event` with `level: "error"` and closes.
* Either side treats 5 s of silence (no packet at all) as a dead connection.
* When the host disconnects, the camera stops capturing after a short grace period (≈ 3 s) to save battery.

## Control messages

All control messages are JSON objects. Unknown fields **must be ignored** and unknown `t` values
must be ignored (forward compatibility). Optional fields may be omitted.

### Common enums

| Name | Values |
|---|---|
| `codec` | `"h264"`, `"hevc"` |
| `orientation` | `"landscape"` (16:9, sensor native, home-side right), `"portrait"` (9:16) |
| `focusMode` | `"continuous"`, `"auto"` (one-shot then lock), `"locked"`, `"manual"` (uses `lensPosition`) |
| `exposureMode` | `"auto"`, `"locked"`, `"manual"` (uses `iso` + `shutter`), `"shutterPriority"` (fixed `shutter`, ISO automatic) |
| `wbMode` | `"auto"`, `"locked"`, `"manual"` (uses `temperature` + `tint`) |
| `stabilization` | `"off"`, `"standard"`, `"cinematic"`, `"cinematicExtended"`, `"previewOptimized"`, `"auto"` |
| `thermal` | `"nominal"`, `"fair"`, `"serious"`, `"critical"` |

### Camera → host

#### `hello`
```json
{"t":"hello","proto":1,"role":"camera","app":"AnyCam","version":"0.1.0",
 "platform":"ios","os":"26.0","model":"iPhone16,1","name":"Kalyan's iPhone","id":"6F1C…"}
```

#### `caps` — what the device can do (sent after `hello` and whenever it changes)
```json
{"t":"caps",
 "cameras":[
   {"id":"com.apple.avfoundation.avcapturedevice.built-in_video:0",
    "name":"Back Camera","position":"back","lens":"wide","zoomFactor":1.0,
    "formats":[{"width":3840,"height":2160,"maxFps":60,"hdr":true},
               {"width":1920,"height":1080,"maxFps":60,"hdr":true}],
    "minISO":32,"maxISO":3072,"minShutter":0.000015,"maxShutter":0.5,
    "minZoom":1.0,"maxZoom":123.75,"zoomSwitchOver":[],
    "hasTorch":true,"focus":true,"manualFocus":true,
    "stabilization":["off","standard","cinematic","cinematicExtended"],
    "maxWBGain":4.0,"lowLightBoost":false}],
 "codecs":["h264","hevc"],
 "audio":true,"photo":true}
```
`lens` is one of `"ultraWide"`, `"wide"`, `"telephoto"`, `"front"`, `"multi"` (virtual multi-lens device).
`zoomFactor` is the lens' magnification relative to the main wide lens (0.5 for ultra-wide, 3.0 / 5.0 for telephoto).
`minShutter` / `maxShutter` are exposure durations in seconds.

#### `state` — current settings and live readings
Sent on every change and at least every 2 s while connected. Every field is optional; the host merges.
```json
{"t":"state","streaming":true,
 "camera":"…","width":1920,"height":1080,"fps":30,"codec":"h264","bitrate":16000000,
 "orientation":"landscape","audio":true,
 "zoom":1.0,
 "focusMode":"continuous","lensPosition":0.62,"smoothFocus":true,"faceDriven":true,
 "exposureMode":"auto","iso":125,"shutter":0.01,"ev":0.0,"exposureOffset":-0.1,
 "wbMode":"auto","temperature":5200,"tint":3,
 "torch":0.0,"stabilization":"off","hdr":false,"lowLightBoost":false,
 "battery":0.82,"charging":true,"thermal":"nominal",
 "interrupted":false}
```
`ev` is the exposure target bias in EV. `exposureOffset` is the metered offset from the target (read-only).
`lensPosition` is 0…1 (0 = closest focus). `torch` is 0…1 (0 = off).

#### `video` — announces the bitstream that follows
```json
{"t":"video","codec":"h264","width":1920,"height":1080,"fps":30,"bitrate":16000000,
 "colorSpace":"bt709","range":"video"}
```
`width`/`height` are the **encoded** dimensions after rotation (1080×1920 for portrait).

#### `audio`
```json
{"t":"audio","sampleRate":48000,"channels":1,"format":"s16le"}
```

#### `pong`
```json
{"t":"pong","id":17,"ts":123456789}
```
Echoes the `ping` fields unchanged.

#### `event`
```json
{"t":"event","level":"warning","code":"thermal","message":"Phone is hot — lowering frame rate"}
```
Known codes: `interrupted` (another app took the camera / phone call), `resumed`, `thermal`, `encoderError`, `unsupported`, `protocol`.

### Host → camera

#### `hello`
```json
{"t":"hello","proto":1,"role":"host","app":"AnyCam Desktop","version":"0.1.0","platform":"windows","name":"KALYAN-PC"}
```

#### `configure` — stream configuration
Omitted fields keep their current value. If the camera is streaming, it restarts the stream with the new settings
(a new `video` message follows).
```json
{"t":"configure","camera":"…","width":1920,"height":1080,"fps":30,
 "codec":"h264","bitrate":16000000,"orientation":"landscape","audio":true,
 "keyframeInterval":2.0}
```
If the exact size/fps is not available, the camera picks the closest supported format and reports it in `state` / `video`.

#### `start` / `stop`
```json
{"t":"start"}
{"t":"stop"}
```

#### `set` — change camera settings
Same keys as `state` (`zoom`, `focusMode`, `lensPosition`, `smoothFocus`, `faceDriven`, `exposureMode`, `iso`, `shutter`, `ev`,
`wbMode`, `temperature`, `tint`, `torch`, `stabilization`, `hdr`, `lowLightBoost`). Only the keys present are changed.
```json
{"t":"set","exposureMode":"shutterPriority","shutter":0.01}
```
Optional `zoomRate` (doublings per second) makes zoom changes ramp smoothly instead of jumping.

#### `focus` — tap to focus / expose
```json
{"t":"focus","x":0.42,"y":0.37,"focus":true,"expose":true}
```
`x`,`y` are normalized coordinates in the **encoded image** (0,0 = top-left). The camera converts them to sensor
coordinates (taking rotation and mirroring into account), sets the points of interest and performs a one-shot
auto focus / exposure, then returns to the previous continuous/locked behaviour.

#### `keyframe`
```json
{"t":"keyframe"}
```
Request an IDR as soon as possible (e.g., after a decoder error).

#### `tally`
```json
{"t":"tally","live":true,"recording":false}
```
`live` = some app is consuming the virtual camera; `recording` = host is recording. The camera shows a tally border.

#### `photo`
```json
{"t":"photo","id":3,"format":"jpeg"}
```
Capture a full-resolution still. The camera replies with a `PHOTO` packet carrying the same `id`.

#### `ping`
```json
{"t":"ping","id":17,"ts":123456789}
```
`ts` is the host's clock in µs; the camera echoes it in `pong` so the host can compute round-trip time.

#### `ui`
```json
{"t":"ui","dim":true}
```
Dim / undim the phone screen (saves battery and heat during long sessions).

## Flow control (camera side)

TCP gives ordered, reliable delivery, so congestion shows up as a growing send queue. The camera must:

1. Track the bytes / frames queued but not yet written to the socket.
2. If the queue holds more than ~0.5 s of video (or 2 MB), **drop video packets until the next keyframe**,
   force a keyframe, and reduce the encoder bitrate (e.g. ×0.7). Never drop part of a frame.
3. When the queue stays short for a while, step the bitrate back up towards the configured value.
4. Audio packets are small and never dropped (unless the connection is dead).

This keeps latency bounded on Wi-Fi without a custom UDP stack, and works unchanged over USB.
