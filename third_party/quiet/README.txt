Horizon Quiet native modem sources

Each upstream tree is pinned in SOURCES.json. Source archive URLs are
https://codeload.github.com/<owner>/<repository>/tar.gz/<commit>.
The original license and copyright files are retained in every tree.

Quiet: BSD-3-Clause. liquid-dsp: MIT. Jansson: MIT.
libfec: GNU Lesser General Public License version 2.1; see libfec/README
and the complete license in libfec/lesser.txt.
Static compiler/startup support carries the LLVM and MinGW-w64 runtime
notices retained verbatim in toolchain-licenses/ from the pinned compiler.

LOCAL CHANGES
quiet/src/ring.c: Repair the upstream non-pthread ring implementation:
include assert.h, make the read destination writable, rename the duplicate
ring_close getter to ring_is_closed, and match the four blocking/nonblocking
stub names to their declarations. The Horizon adapter serializes calls with
a Windows SRW lock; no pthread DLL is required.
No other upstream source files are modified.

BUILD ADAPTATION
libfec is compiled with -Drandom=rand for its unused simulation helper,
because Windows does not supply POSIX random(). The modem's real Viterbi
and Reed-Solomon code is compiled and exercised by synthetic roundtrips.
liquid uses its internal FFT and portable C signal-processing primitives,
with the x64 SSE2 baseline; no build-machine AVX requirement is embedded.

REBUILDING AND REPLACEMENT
Run powershell -ExecutionPolicy Bypass -File scripts/Build-Quiet.ps1 from
the root of quiet-source.zip. It builds the matching sources and validates
real clean and PCM16/noise/gain roundtrips, then emits the DLL, source and
relink packages in build/audio. A portable LLVM-MinGW compiler is downloaded
inside build/ only when it is absent. CMake and Ninja are required; the script
finds Visual Studio Build Tools' copies or accepts explicit paths.

quiet-relink.zip contains the corresponding adapter/Quiet object files and
all three static dependency libraries. The provided Relink-Quiet.ps1 accepts
a replacement libfec.a and links a new DLL without rebuilding Horizon.
quiet-source.zip contains the complete corresponding upstream source plus
the adapter, profiles, tests and build/relink scripts.

Users may modify, debug and reverse engineer the native component as needed
to exercise their rights under its licenses. The application's native DLL
loader must permit replacement of its extracted Quiet DLL; see the portable
application's native-modem replacement instructions.
