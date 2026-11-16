# Windows Notch

A Dynamic-Island-style notch for Windows 11: a small black pill at the top-center of the screen
that expands on hover into a control center.

## What it does

| State | Shows |
|---|---|
| **Idle (collapsed)** | Time · battery % (green bolt when charging) · green dot when the gesture camera is watching |
| **Media playing (collapsed)** | Artwork thumbnail · time · animated equalizer bars tinted with the artwork colour · battery |
| **Lyrics / captions on** | A live line under the pill with the current lyric or caption |
| **Hover** | Expands after a short hover; closes **3 s** after the mouse leaves |

Expanded tabs:

- **Media**: big artwork, title/artist, seekable progress bar, previous/play/next. Click the source chip to switch between Chrome, Spotify, VLC and so on. **Lyrics** shows time-synced lyrics (automatic for Spotify, from LRCLIB). **CC** shows live captions for YouTube, Chrome or any audio, using Windows 11 Live Captions with its window hidden.
- **Apps**: every open window (VS Code, VLC, Terminal, Chrome…). Click to switch to it; hover and press ✕ to close it.
- **Shelf**: drag files, folders or text onto the notch to keep them. Double-click to open, drag out to use, right-click to show in Explorer. Saved between restarts.
- **Control**: Lock · Sleep · Restart · Shut down, the hand-gesture switch, and Start with Windows.

### Camera hand gestures

| Gesture (hand **close** to the camera, held ~1 s) | Action |
|---|---|
| ✊ Closed fist | **Lock** the PC |
| 🖐 Open palm | **Restart**, after a 5-second countdown you can cancel |
| ✊ during a countdown | **Cancel** the restart / shutdown |

- The camera image is never shown or saved. Google MediaPipe runs in a hidden WebView2 and only gesture names come back.
- "Near the camera only": the hand has to fill about 30 % of the frame (`MinHandSize`), so people in the background are ignored.
- The camera is released while the PC is locked, and gestures are never acted on from the lock screen.

## Run

```bash
dotnet build -c Release
```

Then start `bin\Release\net8.0-windows10.0.19041.0\WindowsNotch.exe`. Use **Control → Start with Windows** to have it start at sign-in.

Requirements: Windows 10 2004+ (Windows 11 22H2+ for captions), .NET 8 runtime, and the WebView2 runtime, which comes with Windows 11.
Gestures need **Settings → Privacy → Camera → "Let desktop apps access your camera"** turned on, plus internet the first
time to download the MediaPipe model (about 8 MB, cached afterwards). Captions: if nothing appears, press **Win + Ctrl + L** once to finish Live Captions setup.

## Settings

`%APPDATA%\WindowsNotch\settings.json`:

```json
{
  "GesturesEnabled": true,
  "MinHandSize": 0.30,          // raise to require the hand even closer
  "HoldMs": 900,                // how long to hold a gesture
  "PowerCountdownSec": 5,
  "CollapseDelaySec": 3,
  "LyricsEnabled": true,
  "CaptionsEnabled": false,
  "HideInFullscreen": true      // fades out over fullscreen video (still opens on hover)
}
```

Logs, including each gesture that fired: `%APPDATA%\WindowsNotch\notch.log`.

## Known limits

- **It can't appear on the lock screen.** Windows draws the lock and PIN screen on a separate secure desktop that normal apps aren't allowed to draw on. Only Windows itself or a signed credential provider can. The notch is back as soon as you unlock.
- Media info comes from Windows' media controls. VLC 3.x doesn't report there (VLC 4 does), but VLC still shows in **Apps**.
- Lyrics depend on LRCLIB having the song. Spotify doesn't offer a public lyrics API.
