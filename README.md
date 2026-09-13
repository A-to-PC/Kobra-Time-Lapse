# Kobra Time Lapse

A small standalone Windows app that watches an Anycubic Kobra 3-series printer's own LAN protocol for print state, records a timelapse from any RTSP camera while it prints, and can optionally flag a large sudden change between frames as a possible print failure.

No Moonraker, no Klipper, no OctoPrint plugin, nothing installed on the printer — it talks to the printer's stock firmware directly over your network, the same way Anycubic's own apps do. If you're running Rinkhals with Moonraker, see [3D-Time-Lapse](https://github.com/A-to-PC/3D-Time-Lapse) instead — that's the Moonraker-based sibling of this project; this one is for stock firmware only.

**Status: pre-release.** Confirmed against a real printer: connecting, capturing frames, and assembling the timelapse once a print finishes. Not yet confirmed against real prints: failure detection's SSIM threshold, the opt-in auto-pause, and camera rotation — treat those as early until they've had real testing.

Part of a small family of tools built out of real Kobra 3 Max ownership — see [Kobra 3 Max: The Long Way Round](https://github.com/A-to-PC/Kobra-3-Max-Journey) for the full story of why this exists.

## Why this exists

Stock Kobra 3 firmware has no Moonraker or OctoPrint API for a tool like this to hook into, and running custom firmware (e.g. Rinkhals) just to get one is a real tradeoff — louder fans, harder calibration, and its own set of quirks. This app talks the printer's actual local MQTT protocol instead, reverse-engineered independently, so it works against completely stock firmware with nothing to install or configure on the printer side.

## Features

- **Timelapse** — captures a frame at a set interval while a print is running, assembles `timelapse.mp4` automatically once it finishes, and can delete the individual snapshot frames afterward so completed prints don't leave hundreds of loose JPEGs behind.
- **Failure detection** (optional, off by default) — compares consecutive frames and flags a large sudden change as a possible anomaly. Log-only unless you explicitly also enable auto-pause. Expect this to need real tuning against your own prints and enclosure/lighting setup before trusting it — an enclosure light or roller door changing, or even normal camera sensor noise, can register as a "large change" too.
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

## How it works

- Discovers the printer's MQTT broker address and per-session credentials via the same local handshake Anycubic's own apps use, then subscribes to its live status reports (no cloud account, no credentials hardcoded).
- Watches the reported print state and layer count. When a print starts, it begins capturing; when it ends (by state, or by layer count reaching the total — whichever is more reliable at the time), it assembles the video and stops.
- Grabs each frame straight from the camera's RTSP stream via `ffmpeg`, applying rotation if configured.
- Failure detection compares each new frame against the previous one using `ffmpeg`'s own SSIM (structural similarity) filter — no extra image library needed.

## Building from source

```
dotnet build -c Release
```

The build output includes `ffmpeg.exe` automatically if one is present in the project root at build time — supply your own if you're building from a fresh clone (not included in source control due to its size).

## How this got built

I've spent decades working in IT, and yes, I do use AI (Claude) heavily to write the code in this project — whole features that would've taken weeks or months by hand come together in minutes to hours instead. No apology for that. What's mine is the experience behind every decision: what was actually worth building, telling a real fix apart from one that just sounds plausible, and the judgment to verify every claim against the real printer before it shipped, not take it on faith from a chatbot. The typing speed was never the hard part.

## License

MIT — see [LICENSE](LICENSE).
