# Horizon native camera and microphone receiver

This workspace fork derives from USB to Network Bridge 0.2.19. It retains the
upstream signed virtual controller and the USB UVC/UAC1 interfaces;
the userspace native package version is 0.2.21. No kernel binary is modified.

The authenticated `UBVC` TCP session retains its 48-byte greeting and 44-byte
packet header. Header fields remain a big-endian `u64` sequence, big-endian
`u32` length, and 32-byte HMAC-SHA256. Length bit `0x80000000` denotes 48,000 Hz
mono signed PCM16 little-endian samples; bit `0x40000000` denotes raw YUY2 video.
These flags are mutually exclusive, and the remaining 30 bits give the payload
byte count. HMAC covers the original 12-byte sequence/flagged-length prefix and
payload; all packet types share one increasing sequence. Unflagged JPEG packets
remain compatible with existing senders in the default MJPEG mode.

`camera.uncompressed` defaults to `false`. Each camera configuration advertises
exactly one video format: MJPEG by default, or uncompressed YUY2 when enabled.
That format retains its configured dimensions and maximum/default FPS, and now
advertises discrete rates consisting of the configured rate plus 15, 10 and
5 FPS where these do not exceed the configured maximum. Duplicate rates are
removed. The H.264 probe retains its sole fixed 30 FPS rate. Descriptor lengths,
interval count and minimum/maximum bitrates reflect the complete advertised list.
PROBE is tentative; COMMIT selects the actual USB video pacing independently.
GET_CUR reads the appropriate saved PROBE/COMMIT interval, GET_MIN/MAX report
the fastest/slowest advertised intervals, GET_DEF retains the configured default,
and GET_RES reports a one-100-nanosecond interval field unit with fixed fields
zero. The descriptor's discrete list remains authoritative. Floor/round variants
such as 666666 and 666667 are normalized within 0.01 percent; unadvertised rates
outside that tolerance and malformed control payloads stall without changing
the selected state. A standard configuration-zero reset retires queued video
requests and restores the default intervals; cancellation still wins over reset.
The USB producer reads the committed interval at each frame boundary. Authenticated
TCP ingress and microphone pacing retain their existing configured clocks.
Metadata-only stderr records prefixed `HORIZON_UVC ` include Unix milliseconds,
advertised intervals, bounded PROBE/COMMIT traces, first USB frame and one-second
USB frame/byte summaries. They contain no keys, identity names or frame contents.
Changing descriptors requires refreshing the owned Windows camera instance;
the host application's setup handles migration without replacing its serial.
Greeting FPS bit `0x80000000` identifies YUY2; the remaining 31 bits contain FPS.
Both dimensions must be even, within 16..7680 by 16..4320, with FPS within 1..60.
JPEG frames are bounded by 64 MiB. YUY2 packets must contain exactly
`width * height * 2` bytes and at most 64 MiB; their UVC bitrate must fit the
unsigned 32-bit descriptor field (`width * height * 16 * fps <= 4294967295`).
Thus 3840x2160 permits at most 32 raw FPS and 7680x4320 at most 8 raw FPS.
MJPEG permits 8K at 60 FPS as a configuration, without promising that the
producer, virtual controller, Windows capture stack or Horizon sustains it.

Raw ingress passes YUY2 directly to USB payloads without JPEG encoding or
decoding. Synthetic raw patterns retain one cached frame to bound memory usage.
The managed producer converts RGB using BT.601 limited-range YUY2 pairs; in
MJPEG mode its JPEG quality remains 90 unless explicitly set to 100. The video
interface remains USB 2.0 bulk with 512-byte endpoint packets; no SuperSpeed
descriptor or bandwidth guarantee is introduced. High-resolution modes require
explicit camera reconfiguration; the compatible 1280x720 MJPEG default remains.

`camera.h264_probe` is an experimental third format, default `false` and mutually
exclusive with uncompressed YUY2. It requires exactly 640x480 at 30 FPS and
advertises UVC 1.1 frame-based H264 (format subtype 0x10, frame subtype 0x11).
Greeting FPS bit `0x20000000` identifies it. The backend repeats an embedded
synthetic Annex B frame with SPS/PPS, IDR and a public SEI user-data marker:
UUID `6b82d43f-4d4f-4a2a-9967-0d620761c001`, text `HORIZON_SEI_PROBE_V1`.
This is a fixed diagnostic stream, not a live H.264 encoder. All TCP video
ingress is rejected in this mode; authenticated PCM can still be supplied.
Managed `UvcCamera.IsH264Probe` reports it and `Send(Bitmap)` rejects it before
encoding or writing network bytes. Windows compressed H.264 capture and Horizon
SEI preservation require separate actual-device tests: decoded RGB frames do
not contain the SEI metadata. The fixture SHA-256 is
`6D69DF4C06A352A7E1A0A6F77F7C9F726B15CDED2536DF481968560A7E0892F2`.

PCM chunks must contain 2 through 192,000 bytes, with an even byte count. Audio
is accepted only with `camera.microphone=true`. The FIFO retains at most 960,000
bytes. Overflow discards the oldest complete two-byte samples, keeping the newest
PCM without interrupting video; producers should pace at 96,000 PCM bytes per second. Disconnect clears queued
audio. The USB audio worker consumes PCM using its unchanged absolute 48 kHz
clock, preserves USB/IP offsets and cancellation, pads underruns with zeros, and
returns zeros while muted while consuming queued samples. No physical/default
microphone or audio endpoint is opened by this receiver.

`Build-HorizonNative.ps1` runs offline focused PCM, microphone, raw-video and H.264-probe tests,
formatting, Clippy and a release build. It requires an installed Rust MSVC
toolchain, cached Cargo dependencies, and MSVC/Windows SDK; explicit local paths
may be passed with `-ToolchainRoot`, `-CargoHome`, and `-VcvarsPath`.
The default copy goes to `runtime/usbbridge-camera.next.exe`; the main application
build embeds this as `usbbridge-camera.exe`. It never overwrites an active old
runtime. Standalone server setup migrates the verified older camera only after
the explicit Create action. `-SkipRuntimeCopy` retains the build under
`native/target/release`. Build caches/toolchains are excluded from source inputs.
The WebView build SDK cache is pinned by `webview2-sdk.json` (upstream Microsoft
NuGet URL and per-file SHA-256); populate that local build cache from the pinned
package before an offline build. It is not used or downloaded at runtime.

Example using an installed MSVC Rust toolchain and a writable offline Cargo cache:

```powershell
.\Build-HorizonNative.ps1 -ToolchainRoot C:\Rust\stable-x86_64-pc-windows-msvc `
    -CargoHome C:\BuildCache\cargo -VcvarsPath 'C:\BuildTools\VC\Auxiliary\Build\vcvars64.bat' `
    -SkipRuntimeCopy
```

Tests use synthetic video/PCM and loopback authenticated TCP/USB/IP fixtures.
They verify descriptors, exact raw frames and authenticated type flags without
opening cameras, microphones or clipboard data. They do not establish real
Horizon throughput or modify an installed device.
