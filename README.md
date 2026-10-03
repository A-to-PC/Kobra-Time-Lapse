# Kobra Time Lapse

A small standalone Windows app that watches an Anycubic Kobra 3-series printer's own LAN protocol for print state, records a timelapse from any RTSP camera while it prints, and can optionally flag a likely print failure using a small AI model that runs fully locally.

No Moonraker, no Klipper, no OctoPrint plugin, nothing installed on the printer — it talks to the printer's stock firmware directly over your network, the same way Anycubic's own apps do. If you're running Rinkhals with Moonraker, see [3D-Time-Lapse](https://github.com/A-to-PC/3D-Time-Lapse) instead — that's the Moonraker-based sibling of this project; this one is for stock firmware only.

**Status: released.** Timelapse capture and assembly are confirmed solid against real prints. **Failure detection was rebuilt 03/10/2026** (an AI classifier replacing the previous SSIM frame-diff approach — see "How it works" below) and has **not yet been tested against a real print failure**; the SSIM version's own real-world testing no longer applies to it. **Auto-pause has never actually fired against a real print** under either approach — use it at your own risk: a false positive pauses a perfectly good print, and no real-world false-positive rate is known yet for the AI classifier on your specific setup. Camera rotation also hasn't had real testing yet.

Part of a small family of tools built out of real Kobra 3 Max ownership — see [Kobra 3 Max: The Long Way Round](https://github.com/A-to-PC/Kobra-3-Max-Journey) for the full story of why this exists.

## Why this exists

Stock Kobra 3 firmware has no Moonraker or OctoPrint API for a tool like this to hook into, and running custom firmware (e.g. Rinkhals) just to get one is a real tradeoff — louder fans, harder calibration, and its own set of quirks. This app talks the printer's actual local MQTT protocol instead, reverse-engineered independently, so it works against completely stock firmware with nothing to install or configure on the printer side.

## Features

- **Timelapse** — captures a frame at a set interval while a print is running, assembles `timelapse.mp4` automatically once it finishes, and can delete the individual snapshot frames afterward so completed prints don't leave hundreds of loose JPEGs behind. Capture timing is anchored to each layer change rather than running on its own free clock, so a slow/wide layer naturally gets more frames than a fast/thin one and the result looks steady rather than jittery — confirmed on a real print, no G-code and no added print time involved.
- **Failure detection** (optional, off by default) — classifies each captured frame with a small local AI model (~5MB, runs via ONNX Runtime, no cloud, no frame ever leaves your machine) and flags a likely failure. Ships with a working default model out of the box. Log-only unless you explicitly also enable auto-pause, which is a separate opt-in on top of this — **auto-pause is at your own risk, see the Status note above.** Expect the confidence margin to need real tuning against your own prints and enclosure/lighting setup, same as any detector.
- **Camera rotation** — 0/90/180/270 degrees, for a camera mounted sideways to better frame a tall/narrow printer.
- **Manual mode** — record on a plain interval with Start/Stop, no printer connection needed at all.
- Light/dark theme, following Windows automatically or set explicitly.

Timelapse and Failure Detection are independent — run either one alone, or both together.

## Requirements

- Windows 10/11
- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) (or build from source with the .NET 10 SDK)
- An Anycubic Kobra 3-series printer on stock firmware, reachable on your network
- An RTSP-capable camera (most consumer WiFi/NVR cameras support this once enabled in their settings)
- `ffmpeg.exe` — either on your system `PATH`, or dropped next to the app's `.exe` (the release download includes one)

## Setup

1. Download the latest release, extract it anywhere, and run `KobraTimeLapse.exe`.
2. The Setup window walks through what's needed:
   - **Printer IP** — your Kobra 3's LAN IP address (the same one Slicer Next connects to)
   - **Camera IP, username, password** — the camera's own local RTSP account, which is often separate from any cloud app login
   - **Features** — enable Timelapse, Failure Detection, or both
   - **Save location** — where captured frames and the finished video get saved (a network share or mapped drive works fine)
   - **Camera rotation**
3. Click **Start Watching** and leave it running. Settings are remembered between launches; reopen Setup any time from the main window to change anything.

> **Always use the Stop button, don't close the window directly.** Closing the app while it's still assembling the final video or deleting the source frames kills that work mid-way — the auto-delete setting can't clean up frames it never got the chance to finish deleting. Stop lets any in-progress assembly/cleanup finish properly before the app exits; closing the window doesn't wait for it at all.

## How it works

- Discovers the printer's MQTT broker address and per-session credentials via the same local handshake Anycubic's own apps use, then subscribes to its live status reports (no cloud account, no credentials hardcoded).
- Watches the reported print state and layer count. When a print starts, it begins capturing; when it ends (by state, or by layer count reaching the total — whichever is more reliable at the time), it assembles the video and stops.
- Grabs each frame straight from the camera's RTSP stream via `ffmpeg`, applying rotation if configured.
- Failure detection runs each new frame through a small ShuffleNetV2 encoder (ONNX, ~5MB) and classifies it by nearest-prototype distance against known "success"/"failure" examples — the same approach [PrintGuard](https://github.com/oliverbravery/PrintGuard) uses, and this app's own model/default prototypes are built directly from its real published ones (see License below). All inference is local; nothing is ever uploaded anywhere.

## Building from source

```
dotnet build -c Release
```

The build output includes `ffmpeg.exe` automatically if one is present in the project root at build time — supply your own if you're building from a fresh clone (not included in source control due to its size).

## How this got built

I've spent decades working in IT, and yes, I do use AI (Claude) heavily to write the code in this project — whole features that would've taken weeks or months by hand come together in minutes to hours instead. No apology for that. What's mine is the experience behind every decision: what was actually worth building, telling a real fix apart from one that just sounds plausible, and the judgment to verify every claim against the real printer before it shipped, not take it on faith from a chatbot. The typing speed was never the hard part.

## License

GPL-2.0-only — see [LICENSE](LICENSE). Changed from MIT on 03/10/2026 when the failure-detection
model and its preprocessing/classification code were added, both built directly from
[PrintGuard](https://github.com/oliverbravery/PrintGuard) (also GPL-2.0-only) — specifically its
`models/encoder_float32.onnx`, `metadata.json` and `prototypes.json`, and its preprocessing/
classification logic from `printguard/engine/vision.py`, ported to C#. This app was already free
and open-source, so the practical effect of the license change is limited to the text on the tin.
