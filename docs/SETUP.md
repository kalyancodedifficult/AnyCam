# Setting up AnyCam

You need:

- A **Windows 11** PC (the virtual camera uses a Windows 11 API).
- An **iPhone on iOS 17 or newer**.
- A free **Apple ID** (to sign the iPhone app yourself; no Mac needed).

## 1. Windows app

1. Download **AnyCam-Windows-x64.zip**:
   - from the [latest Windows build](https://github.com/kalyancodedifficult/AnyCam/actions/workflows/windows.yml) (open the newest green run → *Artifacts*; you must be signed in to GitHub), or
   - from the project's *Releases* page once a version is tagged.
2. Extract it to a folder you keep, e.g. `C:\Tools\AnyCam`.
3. Run **AnyCam.exe**.
4. Open the **Output** tab → **Install virtual camera** and approve the Windows admin prompt.
   This copies the camera driver (a normal DLL, not a kernel driver) to `C:\Program Files\AnyCam\VirtualCamera` and registers it.
5. Check that *Settings › Privacy & security › Camera › Let desktop apps access your camera* is **On**.

The bottom-right corner of AnyCam should now say **Virtual camera ready**.

> **USB (recommended for recording):** install **iTunes for Windows** from apple.com (not the Microsoft Store version).
> It includes *Apple Mobile Device Support*, which lets AnyCam talk to the iPhone over the cable. Sideloadly needs it too.

## 2. iPhone app (sideloading, no Mac)

AnyCam isn't on the App Store, so you install it with your own Apple ID. Apple lets a free Apple ID sign apps for **7 days**.
After that the app stops opening until you refresh it (Sideloadly and AltStore can do this automatically).
A paid Apple Developer account makes the signature last a year.

1. Download **AnyCam-unsigned.ipa** from the [latest iOS build](https://github.com/kalyancodedifficult/AnyCam/actions/workflows/ios.yml) (*Artifacts*, then unzip it) or from *Releases*.
2. Install **[Sideloadly](https://sideloadly.io)** on the PC (follow its instructions to install iTunes and iCloud from apple.com).
3. Connect the iPhone with USB, unlock it and tap **Trust This Computer**.
4. In Sideloadly, drop in `AnyCam-unsigned.ipa`, enter your Apple ID and click **Start**.
5. On the iPhone:
   - *Settings › Privacy & Security › Developer Mode* → turn **On**, restart, confirm. (iOS shows this option after the first sideloaded install.)
   - *Settings › General › VPN & Device Management* → your Apple ID → **Trust**.
6. Open AnyCam and allow **Camera**, **Microphone** and **Local Network** access.
   Local Network is required for Wi-Fi: without it the PC can't find or reach the phone.

To avoid the weekly expiry, turn on Sideloadly's *automatic refresh*, or use [AltStore](https://altstore.io) with AltServer running on the PC (it refreshes over Wi-Fi).

## 3. Connect

1. Open AnyCam on the iPhone and on the PC.
2. **Wi-Fi:** with both on the same network, the phone appears in the device list and AnyCam connects automatically.
   If it doesn't, click **Connect by IP…** and type the address shown on the phone (e.g. `192.168.1.23`).
3. **USB:** plug the iPhone in; "iPhone (USB)" appears in the list. USB has the lowest delay and no Wi-Fi hiccups.

The phone shows a red border while an app is using the AnyCam camera or you're recording (a tally light).

## 4. Use it

- **Teams / Google Meet / Zoom / Discord:** choose the camera named **AnyCam** (some apps show "AnyCam (Windows Virtual Camera)"). Restart the app if it was open before you installed the virtual camera.
- **OBS:** *Sources › + › Video Capture Device › AnyCam*. Set *Resolution/FPS type* to *Custom* to pick 1920×1080, 3840×2160 or 1080×1920 (vertical).
- **Record in AnyCam:** the **Record** button (or **F9**) saves the phone's original video and audio to MP4 in *Videos\AnyCam*, with no re-encoding. **Photo** takes a full-resolution still on the phone (great for thumbnails).

## Recommended settings for YouTube

| Situation | Settings |
|---|---|
| Talking head, indoor lights (India/Europe, 50 Hz) | Wide 1×, 1080p or 4K at **25 fps** (or 30 fps with shutter priority **1/50**), focus **Locked** after framing, white balance **Kelvin** (try 4500–5600 K), exposure **Shutter priority** |
| Talking head, daylight | Wide 1×, 1080p/4K 30 fps, exposure **Auto** with **Locked** once it looks right, white balance **Locked** |
| Desk / overhead | Ultra-wide 0.5× or wide 1×, orientation **Landscape**, focus **Manual** + focus peaking |
| Shorts / Reels | Orientation **Portrait** on the phone (native 9:16), or keep landscape 4K and set *Program size* to **1080×1920** to crop |
| Best recording quality | **USB**, **HEVC**, 4K30, 40–60 Mb/s, record with AnyCam's Record button |

Tips:

- Turn on **Zebras** while setting exposure: stripes mean those highlights are clipping (keep them off faces).
- Use **Focus peaking** when focusing manually: the red outline shows what's sharp.
- Turn stabilization **Off** on a tripod: it adds delay and crops the picture.
- Long sessions: take the phone out of its case, keep it charging, and use **Dim** (moon button) on the phone.
- Lip sync: if the iPhone's audio is early or late in another app, adjust *Audio delay* on the Audio tab.

## Use the iPhone as a microphone in other apps

Windows doesn't let apps create a virtual microphone without a signed audio driver. Install the free **VB-Audio Virtual Cable**, then:

1. AnyCam › Audio tab: tick *Play the phone's microphone*, output device **CABLE Input**.
2. In Teams / Zoom / OBS, choose **CABLE Output** as the microphone.

Recordings made with AnyCam's Record button already include the iPhone audio.

## Troubleshooting

| Problem | Fix |
|---|---|
| The PC doesn't see the phone on Wi-Fi | Same network? Guest networks often block devices from seeing each other. Turn off VPNs. Check *iPhone Settings › AnyCam › Local Network*. Use **Connect by IP**. |
| "iPhone (USB)" doesn't appear | Install iTunes from apple.com, unlock the phone and tap *Trust*. Try another cable or port. |
| No "AnyCam" camera in apps | Output tab › *Install virtual camera*; make sure desktop apps may use the camera (step 1.5); restart the app. |
| The app shows "Start AnyCam on this PC…" | That's the virtual camera's placeholder: AnyCam Desktop isn't running or no phone is connected. |
| Picture freezes on Wi-Fi | Lower the bitrate, use 5 GHz Wi-Fi, or use USB. AnyCam skips late frames to stay in sync. |
| "No HEVC decoder" | Install *HEVC Video Extensions* from the Microsoft Store, or switch the codec to H.264. |
| The iPhone app won't open after a week | The free signature expired: refresh it with Sideloadly/AltStore. |
| The phone gets hot | Lower resolution/frame rate, remove the case, use Dim mode. AnyCam lowers the bitrate automatically when iOS reports heat. |

Logs: `%AppData%\AnyCam\crash.log` (PC app). For virtual camera logs, create `C:\ProgramData\AnyCam\logs`; the camera then writes `vcam-*.log` files there.
