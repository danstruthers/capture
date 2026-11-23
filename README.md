# Capture

A Windows console application that captures the screens and audio, records them to MP4 and
streams them to viewers, plus a single-page web viewer for testing and remote control.

- **Screens**: each screen as its own stream, or the whole desktop (every screen, arranged as on
  the desktop) as one stream.
- **Video**: H.264, encoded once per stream and used for both the MP4 files and the live stream.
  A hardware encoder is used when the machine has a working one; otherwise the Microsoft software
  encoder.
- **Audio**: mixed from any number of audio devices, recording devices (microphones, line in) and
  playback devices (recorded as what they play), into 16-bit PCM at 8 kHz mono; by default, the
  Windows default microphone. That is stored as AAC in the MP4 files and sent as G.711 over
  WebRTC, or as the PCM itself over the WebSocket.
- **Streaming**: WebRTC (H.264 + G.711), or as a fallback JPEG pictures and raw PCM over a WebSocket.
- **Remote control**: viewers can start, stop, pause and resume recording and streaming, change the
  frame rate, switch the screen layout and the video method, and see every audio device and choose
  which ones are recorded.
- **Viewer**: shows each screen in its own tile; you choose which screens to watch, zoom each one
  with the mouse wheel, and pop any of them out into a window of its own.

Media Foundation does the conversion, encoding, resampling and MP4 writing, through the
[MediaFoundation](https://www.nuget.org/packages/MediaFoundation) wrapper package.

## Requirements

- Windows 10 or later, with an interactive desktop session
- .NET 8 SDK
- A current Chromium-based browser or Firefox for the viewer

## Quick start

```powershell
dotnet build

# Terminal 1: the capture client (records every screen and waits for viewers)
dotnet run --project src/Capture

# Terminal 2: the viewer page
dotnet run --project src/Capture.Viewer
```

Open <http://localhost:5174> and press **Connect**. Stop the capture client with Ctrl+C; that
finalizes the MP4 files, which are in your TEMP folder unless you set `--output-dir`.

Useful variations:

```powershell
capture --list-devices                    # screens, audio devices, H.264 encoders
capture --layout desktop                  # all screens as one stream
capture --screens 1                       # only screen 1
capture --no-record --no-stream           # start idle; begin from the viewer
capture --fps 15 --max-width 1280         # less CPU and bandwidth
capture --audio-source both               # default microphone + what the default speakers play
capture --audio-device 2 --audio-device Headphones   # particular devices (numbers from --list-devices)
capture --video-mode jpeg                 # JPEG pictures instead of WebRTC
capture --no-server -o demo.mp4           # just record (to %TEMP%\demo.mp4)
capture --output-dir D:\Recordings        # save recordings somewhere other than TEMP
capture --bind 0.0.0.0 --allow-origin http://192.168.1.20:5174   # viewers on other machines
```

`capture --help` lists every option.

## Configuration

Settings can come from `capture.json`, looked for in the current directory and then next to
`capture.exe`. Its keys are the command-line option names without the dashes (`videoMode` or
`video-mode`), and the command line overrides the file; a list given on the command line
(`--audio-device`, `--allow-origin`, `--screens`) replaces the file's. On/off options can be
written `"record": false` in the file and `--record`, `--no-record` or `--record=false` on the
command line. The `capture.json` that comes with the program has every setting commented out, so
the program's own defaults apply until you change one.

```jsonc
{
  "layout": "separate",          // "separate" (a stream per screen) or "desktop" (one stream)
  "screens": [1, 2],             // which screens; leave out for all
  "fps": 30,                     // 1-60; viewers can change it while running
  "videoMode": "h264",           // "h264" (WebRTC) or "jpeg" (pictures over the WebSocket)
  "audioSource": "microphone",   // Windows' default devices: "microphone", "desktop", "both" or "none"
  "audioDevice": ["Headset"],    // particular devices, by number or part of the name (see below)
  "outputDir": "%TEMP%",         // where recordings are saved (the default)
  "record": true,                // start recording at launch
  "stream": true,                // start streaming at launch
  "bind": "127.0.0.1",
  "port": 8081
}
```

Screens are numbered from 1, in the order `capture --list-devices` shows them.

Audio comes from Windows' default devices unless particular ones are named: `"microphone"` is the
default recording device, `"desktop"` what the default playback device plays. Each `audioDevice`
is recorded as well; a recording device named there takes the place of the default microphone,
and a playback device that of the default desktop audio. Viewers can change all of this while
the capture client runs.

## The viewer

`src/Capture.Viewer` is a small host that serves one static page (`wwwroot`: HTML, CSS and a few
JavaScript modules, no build step or dependencies). The page talks to the capture client directly.
`viewer.js` is the page; `protocol.js` has the messages, `zoom.js`, `pcm-player.js` and
`format.js` the parts that stand on their own.

- **Screens**: one tile per stream, arranged the way the screens are arranged on the desktop.
  Each tile shows its size, frame rate and bit rate.
- **Watch**: tick the screens to show in this window. Screens nobody watches are not sent, and if
  nothing else needs them (no recording) they are not captured at all.
- **Zoom**: scroll the mouse wheel over a screen to zoom in around the pointer, drag to move
  around, double-click (or click the percentage) to go back to 100%.
- **Pop out**: opens the screen in a window of its own, which you can move to another monitor,
  resize or make full screen. Closing that window (or **Bring back** under Watch) returns the
  screen to the main window. A pop-out window has its own connection to the capture client, asks
  for only its screen and plays no sound, so audio is never heard twice.
- **Remote control**: Start / Pause / Stop for recording and for streaming, the layout, the video
  method and the frame rate. The panel always shows the capture client's actual state.
- **Audio devices**: every recording device and every playback device of the capture client's PC,
  each with a tick box: ticked devices are mixed into the audio. Each group starts with **Default
  device**, which follows Windows' default. Under each device: whether it is live (and the format
  it delivers), starting, idle (nothing is being recorded or watched), or why it cannot be
  recorded. The list is refreshed every couple of seconds, so plugging in a headset shows up.
- `?url=ws://host:8081/` presets the address and `?autoconnect` connects on load.
- In the browser console, `captureViewer.stats` and `captureViewer.state` hold the latest numbers.

## How it works

```
for each stream (each screen, or the whole desktop):
  screen(s) ──► capture (DXGI desktop duplication per screen, GDI fallback) ──► BGRX frame
                  │
                  ├─► video processor MFT (BGRX → NV12, scaling) ─► H.264 encoder MFT ─┬─► MP4 sink writer
                  │                                                                    └─► WebRTC track
                  └─► JPEG encoder ────────────────────────────────────────────────────► WebSocket

each recording device (MF source reader) ─► resampler MFT ─┐
each playback device (WASAPI loopback) ───► resampler MFT ─┴─► mixer: 20 ms frames, 8 kHz PCM ─┬─► AAC in every MP4
                                                                                               ├─► G.711 over WebRTC
                                                                                               └─► PCM over WebSocket
```

- **Nothing runs unless it is needed.** A stream is only captured while it is recorded or while a
  viewer is watching it, and audio devices are only open while something uses them. An idle
  capture client uses no CPU and does not hold the microphone.
- **One clock.** Video frames are stamped when captured; audio is placed by the devices' own
  timestamps on the same clock, so sound and pictures line up across all streams, in the files and
  in the stream.
- **Audio devices** open in the background, side by side, and each joins the mix when it is ready:
  some take seconds to start (a monitor's speakers over HDMI have to wake up), and that holds up
  neither the other devices nor whatever turned audio on. While audio is in use the devices are
  looked at every two seconds. A device that stopped or was unplugged is opened again when it is
  back; one that will not open is tried again less and less often, up to once a minute. A **Default
  device** entry moves to the new default when Windows' default changes, and if it and the device
  it stands for are both ticked, that device is recorded once.
- **The desktop stream** composes the screens into one picture as they are arranged; parts of the
  desktop rectangle no screen covers (screens of different heights) are black. H.264 encoders take
  at most 4096 × 2304, so a wider desktop is scaled down to fit (two side-by-side screens of 2560
  and 1920 pixels become 4096 × 1316).
- **HDR screens.** In HDR mode Windows composes the desktop in 16-bit floating point (scRGB), with
  SDR content scaled up to the *SDR content brightness* setting. The 8-bit picture desktop
  duplication normally hands over makes no allowance for that and looks far too bright and pale.
  For a screen in HDR mode the capture client takes the 16-bit picture instead and converts it back
  on the GPU (a compute shader): divide by the SDR white level, encode as sRGB. SDR content comes
  out with the values it was drawn with; HDR content brighter than SDR white (HDR video, games) is
  clipped to white. The white level is read again every two seconds, so moving the slider takes
  effect while running, and switching HDR on or off is picked up by itself.
- **Encoder choice** (`--encoder auto|hardware|software`). Each stream has its own encoder. Each
  hardware H.264 encoder MFT is tried in merit order and has to pass a short self-test (produce a
  key frame from synthetic frames) before it is used. If none passes, or one fails later while
  running, the software encoder takes over. Consumer GPUs limit how many hardware encoding sessions
  run at once; with the default `--encoder auto`, streams beyond that limit fall back to the
  software encoder.
- **Frame rate changes** replace each encoder with one configured for the new rate; the streams
  continue from their first key frame. A recording in progress continues in a new file if the new
  encoder's parameters differ.
- **Layout changes** set the streams up again from scratch: viewers get a new WebRTC session with
  the new set of tracks, and a recording in progress continues in new files. Setting the layout
  also looks the screens up again, so it picks up screens that were added, removed or rearranged.

### Recording

MP4 with H.264 video (the encoder's output, written without re-encoding) and AAC audio. The MPEG-4
sink cannot hold linear PCM and the AAC encoder only runs at 44.1/48 kHz, so the 8 kHz PCM is
upsampled to 48 kHz and encoded as AAC-LC.

- Files are saved in the TEMP folder unless `--output-dir` (`"outputDir"` in `capture.json`)
  says otherwise. The folder may use environment variables (`%USERPROFILE%\Videos\Capture`) and
  is created if needed. The capture client logs it at startup, and the viewer shows each file's
  full path while recording. Windows can empty TEMP on its own (Storage Sense, Disk Cleanup), so
  choose another folder for recordings you want to keep.
- One file per stream, each with the audio: `capture-<date>-<time>-screen1.mp4`,
  `...-screen2.mp4`; with a single stream (the desktop layout, or `--screens 1`) just
  `capture-<date>-<time>.mp4`. With `-o name.mp4` the files are named after `name`; a relative
  `-o` is in the output folder, a full path is used as it is.
- Every start makes new files (`name-2-screen1.mp4`, ...), and so does a layout change while
  recording.
- A pause leaves no gap: the files' timeline continues at the point of resuming.
- An MP4 file is only playable once finalized (on stop, or Ctrl+C). `--segment <seconds>` starts
  new files every N seconds (`name-screen1-001.mp4`, ...) so that a crash costs at most one segment.
- A stream's video can change format while recording: a new frame rate, or the software encoder
  taking over from a hardware encoder that failed. An MP4 video track holds one format, so the
  recording carries on in a new file, `name-screen1-part2.mp4`, `-part3`, ... (or the next
  segment, with `--segment`).

### Video methods

| | H.264 (`h264`) | JPEG (`jpeg`) |
|---|---|---|
| Transport | WebRTC (UDP) | the WebSocket (TCP) |
| Video | H.264, one RTP track per stream | one JPEG per picture |
| Audio | G.711 µ-law, 8 kHz | 16-bit PCM, 8 kHz |
| Use | normal viewing | where WebRTC or H.264 is not available |

JPEG pictures are far larger than H.264 frames: a full-size 2560×1440 screen at 10 fps is around
10–20 Mbit/s. Lower `--fps`, `--max-width` or `--jpeg-quality` to reduce it.

## WebSocket protocol

A client connects to `ws://<host>:8081/`. Text messages are JSON, with camelCase names and
values (`"h264"`). The code that defines them is
[`ViewerProtocol.cs`](src/Capture/Output/ViewerProtocol.cs); this section describes the same.

From the capture client:

| Message | Meaning |
|---|---|
| `{"type":"state", "remoteControl":true, "layout":"separate", "recording":{…}, "streaming":{…}, "video":{…}, "sources":[…], "audio":{…}}` | Current state; sent on connect, on every change and once a second. |
| `{"type":"error","message":"…","request":7}` | A request was refused, or failed. `message` is written for the person who asked; `request` is the request's `id`, if it had one. |
| `{"type":"offer","sdp":"…","sources":{"1":"screen1","2":"screen2"}}` | WebRTC offer (H.264 mode), with the stream each video section (by `mid`) carries. Reply with the answer. |
| `{"candidate":"…","sdpMid":"…","sdpMLineIndex":0}` | ICE candidate. |

The parts of the state:

| Part | Fields |
|---|---|
| `recording` | `state` (`stopped`, `recording`, `paused`), `files` (the file names being written), `seconds` (recorded, pauses left out), `bytes` |
| `streaming` | `state` (`stopped`, `streaming`, `paused`), `viewers` (clients being sent media), `clients` (clients connected) |
| `video` | `mode` (`h264` or `jpeg`), `frameRate`, `jpegQuality` |
| each of `sources` | `id` (`screen1`, `screen2`, … or `desktop`), `number` (the screen number, 0 for the desktop), `name`, `screens`, `primary`, position and size on the desktop (`left`, `top`, `width`, `height`), the encoded size (`videoWidth`, `videoHeight`), `bitrateKbps`, `capturing` (whether anything uses it), `viewers` (clients receiving it), `capture` (the capture method), `encoder` and `hardware` |
| `audio` | `enabled`, `selected` (the ids being recorded), `source` (`microphone`, `desktop`, `both` or `none` when the selection is just default devices, otherwise `devices`), `description`, `sampleRate`, `devices` |

Each entry of `audio.devices`:

- `id`: the Windows endpoint ID, or `default-input` / `default-output` for the entries that stand
  for Windows' default recording and playback device;
- `kind` (`input` for recording devices, `output` for playback devices), `name`, `default`
  (whether it is Windows' default), `present` (connected), and for the default entries `device`,
  the name of the device they stand for;
- `selected`, `status` (`off`, `idle`, `starting`, `capturing` or `unavailable`), `problem`
  (why it is unavailable) and `format` (what it delivers while captured).

To the capture client:

| Message | Meaning |
|---|---|
| `{"type":"select","sources":["screen1"],"audio":true}` | Which streams this client wants, and whether it wants sound. Until a client says, it gets everything. Allowed even when remote control is off. |
| `{"type":"command","target":"recording","action":"start","id":7}` | `target`: `recording` or `streaming`; `action`: `start`, `stop`, `pause`, `resume`. |
| `{"type":"configure","frameRate":15,"id":8}` | Also `"layout":"separate"\|"desktop"`, `"videoMode":"h264"\|"jpeg"`, `"audioDevices":["default-input","{0.0.0.00000000}.{…}"]` (exactly the devices to record) and `"audioSource":"microphone"\|"desktop"\|"both"\|"none"` (shorthand for the default devices). Every setting is checked before any is applied: a request with a mistake in it changes nothing. |
| `{"type":"answer","sdp":"…"}` | WebRTC answer. |
| `{"candidate":"…","sdpMid":"…","sdpMLineIndex":0}` | ICE candidate. |

`id` is optional; an `error` refusing the request carries it back, so a client can tell which of
its requests failed. A client can also choose up front, in the address:
`ws://host:8081/?select=screen2&audio=0`.

In JPEG mode, media arrives as binary messages: a type byte, the stream number, a 64-bit
little-endian timestamp in 100 ns units, then the payload.

| Type | Payload |
|---|---|
| `1` | A JPEG picture of the stream with that number. |
| `2` | 20 ms of 16-bit little-endian PCM, 8 kHz mono (stream number 0). |

## Security

- The server listens on `127.0.0.1` unless `--bind` says otherwise.
- Browsers may only connect from pages served from localhost, plus any origin given with
  `--allow-origin`. (A WebSocket is not protected by the browser's same-origin policy, so without
  this check any web page could read the screens.) Programs that are not browsers are not
  restricted by this.
- There is no authentication and no TLS. Anyone who can reach the port can watch and, unless
  `--no-remote-control` is given, control the capture. Do not expose it to an untrusted network.

## Tests

```powershell
dotnet test                 # unit and Media Foundation round-trip tests
./tests/EndToEnd.ps1        # capture client + viewer in headless Edge, driven through every control
```

The round-trip tests encode synthetic frames, record them and decode the MP4 files again to check
the picture (colours, orientation), frame counts, durations, audio, one file per stream, and new
files after a change of streams. The screen capture tests compare desktop duplication with GDI,
for one screen and for the composed desktop (on an HDR screen this checks the HDR conversion
against what apps drew). The HDR conversion is also tested on its own with synthetic scRGB
pictures, on WARP (Windows' software rasterizer), so it is covered on any machine.

Hardware encoders cannot be tested on a machine without one, so the asynchronous encoder path is
exercised with a simulated asynchronous transform wrapped around the software encoder
(`AsyncTransformSimulator`).

The audio mixer is tested with stand-in devices (which devices are open when, default-device
entries, refusals, slow devices, devices that stop or will not open, the mix itself) and with
this PC's real devices: every connected device is recorded at once. The viewer protocol (what is
accepted and refused, the state's JSON, the binary framing), the origin check, RTP packetization
and the command-line options have tests of their own.

Some tests need things not every machine has: an interactive desktop, desktop duplication, two
screens, audio devices. Without them those tests are reported as skipped, with the reason; they
never pass without checking.

The end-to-end test also chooses screens, zooms, pops a screen out into its own window and back,
ticks and unticks audio devices, and switches between the two layouts. It runs the capture client
with an empty configuration file, so a local `capture.json` does not change what it tests. When a
check fails it shows the logs and keeps its output folder.

## Layout

```
src/Capture/            the capture client: options, the capture session that wires it together, the status line
  Video/                screens and capture areas, capture (desktop duplication, GDI, HDR conversion),
                        conversion, H.264 encoders and JPEG, the video pipeline
  Audio/                audio devices and selection, recording-device and loopback sources, timeline, mixer, resampler
  Output/               MP4 recorder; stream server, one connection per viewer, WebRTC sessions, RTP
                        packetization; the viewer protocol and the audio device list viewers see
  Interop/              Media Foundation helpers; ICodecAPI, Direct3D/DXGI, Core Audio (WASAPI) and Win32 declarations
src/Capture.Viewer/     static host and the viewer page (wwwroot)
tests/Capture.Tests/    unit and round-trip tests
tests/EndToEnd.ps1      end-to-end smoke test
```

## Notes and limits

- Rotated screens, and sessions without desktop duplication, are captured with GDI, which is much
  slower at high resolutions.
- HDR screens are captured as SDR: the recording and the stream are 8-bit SDR video, and HDR
  highlights are clipped. A graphics adapter older than Direct3D 11 cannot run the conversion;
  the capture client then warns that the screen will look too bright (`--capture gdi` avoids it).
- Capture cost is dominated by reading each frame back from the GPU; every stream adds its share,
  and the desktop stream also pays for scaling when it is wider than 4096 pixels. The status line
  shows the time per frame of each stream.
- Changes to the screens (a screen added or removed, a resolution change) are picked up when the
  layout is set again (from the viewer, or by restarting), not automatically.
- Hardware encoders are fed system-memory NV12 frames; there is no Direct3D zero-copy path.
- Every audio device is reduced to 8 kHz mono and the devices are simply added together, so
  several loud devices at once can clip.
- A playback device is recorded as what Windows sends to it; a device nothing is playing on
  records silence.
- WebRTC sending has no congestion control: it suits a LAN, not a constrained link.

## Third-party components

- [MediaFoundation](https://www.nuget.org/packages/MediaFoundation) 3.1.0 (MediaFoundation.NET,
  LGPL 2.1). It is a .NET Framework build used through .NET 8's COM interop.
- [SIPSorcery](https://github.com/sipsorcery-org/sipsorcery) (WebRTC). Its licence is BSD 3-Clause
  with an additional use restriction; read its `LICENSE.md` before distributing.
- System.Drawing.Common (GDI+ JPEG encoding).
