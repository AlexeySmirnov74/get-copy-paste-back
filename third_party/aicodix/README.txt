Horizon aicodix audio backend

This directory vendors the real aicodix OFDM modem and its two header-only
dependencies. All three upstream projects use the Zero-Clause BSD (0BSD)
license. Their original license texts are retained in each subdirectory.

Pinned sources, retrieved 2026-11-13:
  https://github.com/aicodix/modem/tree/6f0ff063a94b28487822c57ba921aebe82abf7ac
    branch next; version 6f0ff063a94b28487822c57ba921aebe82abf7ac
  https://github.com/aicodix/dsp/tree/f268a558e801cbe10a986a17d7d09985673e159d
    version f268a558e801cbe10a986a17d7d09985673e159d
  https://github.com/aicodix/code/tree/adb760f338f39d7a2d29628b2edca331f887c5e3
    version adb760f338f39d7a2d29628b2edca331f887c5e3

The snapshots include upstream root headers, licenses, READMEs, Makefiles,
and modem reference encoder/decoder source. Website files, binary WASM
artifacts, test executables and unrelated CLI sources are omitted.
UPSTREAM-FILES.json records SHA-256 hashes of the vendored files.

Local compatibility change:
  dsp/kahan.hh: add an _MSC_VER branch to the compiler-specific annotation
  selection. Horizon compiles with /fp:precise so the Kahan compensation
  terms retain the required evaluation order. There is no codec/protocol
  change. All other vendored files are unchanged.

Build and verify (Visual Studio C++ x64 Build Tools required only to rebuild):
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts\Build-Aicodix.ps1

The output build\audio\horizon-aicodix.dll is Windows x64 with its C/C++
runtime statically linked (/MT). End users need no compiler, Python, Node,
WAV utility, package manager or separate runtime installation. The build
reads only vendored source and performs no network requests.

native\audio\aicodix.h defines the exported C ABI. Audio is normalized mono
float PCM at 48000 samples/sec. Encoding writes directly to the caller's
PCM buffer; decoding feeds samples directly into the upstream synchronizer
and decoder. There are no temporary audio files or Unix standard-I/O paths.

Mode 0: QAM16, 3/4 polar FEC, normal frame (upstream mode 0x35).
  Native frame 1536 bytes; maximum ABI message 1528 bytes.
  Encoded frame 140800 samples / 2.933333 seconds.
  Full-message useful rate: 4167.27 bit/sec (before Horizon wire overhead).
Mode 1: QAM256, 3/4 polar FEC, normal frame (upstream mode 0x55).
  Native frame 3072 bytes; maximum ABI message 3064 bytes.
  Encoded frame 140800 samples / 2.933333 seconds.
  Full-message useful rate: 8356.36 bit/sec (before Horizon wire overhead).

The wrapper prefixes each native padded frame with eight bytes:
  bytes 0..3: ASCII HCA1
  bytes 4..5: exact message length, little endian
  bytes 6..7: one's complement of that 16-bit length, little endian
The native modem supplies its own payload CRC32 and polar error correction.
The wrapper accepts 1..hc_max_payload(mode) bytes and preserves embedded
NULs and trailing zero bytes. A zero-length message is invalid because zero
from hc_receive means no available message.

hc_create accepts mode 0 or 1 and sample rate 48000. Each decoder must be
used by one serialized stream. Decoded messages are copied into a bounded
32-message queue. If full, the oldest message is discarded and hc_feed
returns -4 after processing the capture block. hc_receive with insufficient
capacity returns -2 and leaves the message queued. Other negative codes
are documented in the header.

The upstream decoder intentionally tracks several symbols behind live
capture. Continue capture after playback; allow 0.5 seconds of silence
when running finite synthetic PCM tests. The encoder includes 100ms of
silence at each end; this does not replace continued microphone capture.

The native roundtrip executable checks both modes, repeated frames on one
decoder, one-byte/random/full-capacity payloads, arbitrary chunk boundaries,
16-bit audio quantization, 0.67 gain, invalid arguments, capacity errors,
three adjacent frames with silence only at the end, and queue overflow.
This verifies software transport. It does not measure a physical acoustic
link, speakers/microphone distortion, AGC, echo cancellation or room noise.
