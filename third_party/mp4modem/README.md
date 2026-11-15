# MP4Modem V2 visual transport port

Upstream: https://github.com/grmchn/mp4modem

Pinned commit: `b3f2d2d1fdbba17104ba473170f7111100e10aea` (2026-08-09).
License: Apache-2.0; the complete license and original source files are under
`upstream/`. The source snapshot is retained to make the port reviewable without
a network connection. `UPSTREAM-FILES.json` records the pinned Git blob IDs.

`src/Mp4ModemProtocol.cs` and `src/Mp4ModemReedSolomon.cs` port the upstream
16x16 low frequency four-basis waveform, 192 calibration pilots, three compact
tag copies, CRC-32, RS(255,191), and column interleave to C#/.NET Framework.
No Python, Node, FFmpeg, browser, or external codec process is needed at runtime.
The 1280x720 basis I420 frame and the RS encoder are verified against the
upstream Python/TypeScript golden hashes in `test-vectors/transport-v2.json`.

The Horizon adapter is an explicit integration format. Each video frame contains
one independently protected Horizon packet. It does not encode MP4 containers,
Payload Bundles, or the upstream multi-frame control schedule. Upstream Pack
files cannot be opened by this adapter and Horizon frames cannot be opened by
the upstream Bundle decoder.

## Horizon frame format

The compact tag uses upstream profile `0x81`, version 2, epoch 0, phase 0,
flag `0x02` (Horizon packet adapter), and symbol ID equal to the packet index.
The visual waveform and compact-tag encoding are byte-compatible with upstream.

Six RS blocks each protect 187 bytes plus a big-endian CRC-32; each is encoded
as RS(255,191). Codewords are interleaved by column, giving 1530 coded bytes.
These bytes are padded to 1652 bytes, followed by the upstream big-endian
frame CRC-32, filling the 1656-byte basis payload. A damaged frame CRC does not
prevent RS repair; every decoded block CRC and the Horizon packet CRC must pass.

The 1122 uncoded bytes contain a 36-byte header, up to 1082 payload bytes,
then a little-endian CRC-32 of the header and payload. Remaining bytes are zero.
Header fields:

| Offset | Size | Meaning |
| --- | --- | --- |
| 0 | 4 | ASCII `M4HC` |
| 4 | 1 | Adapter version 1 |
| 5 | 1 | Horizon profile ID 11 |
| 6 | 2 | Payload length, little-endian |
| 8 | 16 | Transfer GUID in .NET `Guid.ToByteArray()` order |
| 24 | 4 | Zero-based packet index, little-endian |
| 28 | 4 | Packet count, little-endian |
| 32 | 4 | Total application payload length, little-endian |

The adapter validates packet counts, exact final-chunk length, the 32 MiB
application limit, and agreement between tag symbol ID and packet index.
The pilot-calibrated bitmap decoder supports centered 16:9 frames, rescaling
to at least 320x180, and a full-frame fallback for an aspect-ratio stretch;
arbitrary perspective, mirroring, rotation, or an off-center crop is unsupported.
Camera/Horizon compression compatibility depends on the actual negotiated path.

## Synthetic codec benchmark

`tests/Mp4ModemCompressionBench.cs` generates 30 independent 1082-byte packets,
encodes each as a 1280x720 frame, and passes them through local FFmpeg/libx264.
It tests 1280x720, 640x360, and a hard aspect-ratio resize to 320x240 at requested
1, 2.6, 5, and 10 Mbps. The settings are `veryfast`, `zerolatency`, YUV420,
30 FPS, a 100 ms VBV buffer, no B frames, and a 30-frame GOP. Output includes the
actual elementary H264 stream bitrate, exact valid unique packets, useful
payload rate normalized to one nominal second, and separate protocol/codec
timings. Every decoded GUID/index and payload byte is compared with the
generated input; rejected frames and duplicate packets earn no useful bytes.

Run the compiled development harness as:

```text
build\Mp4ModemCompressionBench.exe C:\path\to\ffmpeg.exe build
```

The result is `build/video-mp4modem-h264.csv`. Intermediate synthetic media are
removed after the run. This experiment does not exercise the Horizon/RDP path,
camera redirection, device negotiation, real-time frame loss, or a remote CPU.
