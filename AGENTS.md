# Working on Get Copy-Paste Back

## Architecture and scope

- `src/` is a C# 5, x64 .NET Framework / Windows Forms application. `scripts/Build.ps1` compiles the common sources twice: `Server.exe` with `HORIZON_SERVER`, and `Client.exe` without it. Do not assume a solution file, modern language features, or `dotnet build`.
- The **server runs locally** and publishes clipboard payloads through its virtual camera. The **client runs inside the remote desktop**, captures Horizon RTAV video, and publishes the received clipboard. Remote-to-local payloads travel through a selected remote audio output, Horizon audio, and the server's loopback capture of all active playback devices or one selected device.
- `ClipboardPayload.cs` owns bounded serialization, compression, path validation, and file staging. Keep CRC, transfer/session binding, bounds, duplicate suppression, cancellation, and clipboard echo prevention intact.
- `MemoryFileClipboard.cs` supplies client-received files through bounded in-memory Shell `FileGroupDescriptorW`/`FileContents` streams. Never reintroduce client disk staging or a `Received` cache. Keep storage-medium ownership, independent stream positions and clipboard-busy retries intact. The client must run until paste completes; destinations requiring `CF_HDROP` paths may not support virtual files. Server reverse-file staging is separate.
- `VisualProtocol.cs`, `DenseVideoProtocol.cs`, `Mp4ModemProtocol.cs` and `ColorVideoProtocol.cs` own visual packets. Five ordinary UI modes are always offered: Fast/720p, Dense/720p dense, HighDefinitionRobust/1080p robust, Compact640/640p and Mp4Modem. The server's persisted `colorModesEnabled` checkbox optionally adds Color720/Color720Dense/Color1080/Color1080Dense (wire IDs 12-15), also to its speed test. It defaults off; a legacy experiment's saved color selection enables migration unless explicitly disabled. Historical enum/decoder entries do not make other profiles public features.
- `AudioModem.cs` is managed FSK. `AudioTransport.cs`, `NativeAudioCodec.cs`, `AudioPacketWire.cs`, and `native/audio/` adapt Quiet and aicodix. `ClientAudioBridge.cs` and `ServerAudioBridge.cs` coordinate control and data. The server defaults to all active local render outputs with separate decoders and shared packet deduplication; the client still sends through one selected output.
- `native/usbbridge/` is the Rust camera backend; its component version is separate from the Get Copy-Paste Back application version. `ServerRuntime.cs`, `DriverInstaller.cs`, and the camera PowerShell scripts own offline resources and the explicit camera lifecycle.
- Server form code is split across `ServerForm.*.cs`; the tray client is `ReceiverContext*.cs`. Update English and Russian UI text together. `README.md` is the simple public English entry point; keep `README.ru.md` and `docs/*.svg` consistent. Detailed architecture, speeds, storage, build prerequisites and component terms belong in `docs/TECHNICAL.md` and `docs/TECHNICAL.ru.md`. Store original UI screenshots in `screenshots/`; both root READMEs embed screenshots 2 and 3 and link to the complete gallery.
- `ClientPaths.cs` selects the client's writable LocalAppData/Roaming/EXE fallback settings directory. Client audio libraries belong in its `audio-native` child. The shared green icon is `assets/HorizonClient.ico`, with SVG source and the offline `scripts/Build-AppIcon.ps1` generator.
- The public product is **Get Copy-Paste Back**, repository `AlexeySmirnov74/get-copy-paste-back`, with `Server.exe` and `Client.exe`. Retain legacy `HorizonServer/` runtime paths, `HorizonClient/Settings` paths, C# namespaces, resource identifiers, helper filenames and component names for compatibility. Historical release notes and plans keep the names used at their release date.
- `CameraNames.cs` supplies the same 100-name catalog to both roles. Client automatic discovery tries the saved exact device ID first, including a manually selected custom name; other candidates require an exact catalog-name match. A name is only a filter: accept a camera only after validated wire control. Unsaved noncatalog devices must not be opened automatically. Catalog-matching physical cameras can still open briefly. Bound each candidate to one finite compatibility pass; preserve exhaustion until explicit automatic off/on or client restart, while allowing newly appearing IDs. Manual selection disables automatic selection and persists.

## Build and validation

Use Windows x64 and Windows PowerShell. The managed compiler is `%WINDIR%/Microsoft.NET/Framework64/v4.0.30319/csc.exe`; the build uses C# 5 and Windows Forms/System.Drawing. Runtime resources must exist before the managed build: native audio DLLs plus Quiet source/relink archives, the camera backend, and the pinned signed driver installer. See [the technical guide](docs/TECHNICAL.md) for prerequisites.

```powershell
# Full managed compilation and synthetic/fixture suite; no release-file copy.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts\Build.ps1 -NoPublish
```

`-SkipTests` is permitted for an intermediate build, never as release evidence. The build compiles `CameraSmoke.exe` and `AudioSmoke.exe` but does not run them. Fixture tests may create dedicated temporary files and hidden forms; preserve their injected clocks/services and isolated paths. Add meaningful tests for protocol, lifetime, interoperability, or validation changes, using synthetic data and fake endpoints.

PowerShell 5.1 requires a UTF-8 BOM on scripts containing non-ASCII text. The managed build checks owned `scripts/` and `tests/` files before compilation; keep that preflight enabled so English and Russian Windows decode fixtures identically.

Native rebuilds require MSVC x64 C++ Build Tools/Windows SDK, Rust with Cargo/rustfmt/Clippy, CMake, and Ninja:

```powershell
# Initial dependency fetch requires network; the backend helper then uses --offline --locked.
cargo fetch --locked --manifest-path native\usbbridge\Cargo.toml
powershell.exe -NoProfile -ExecutionPolicy Bypass -File native\usbbridge\Build-HorizonNative.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts\Build-AudioNative.ps1
```

The native backend helper runs formatting, selected synthetic tests, Clippy, and the camera backend release build. Before an offline Rust build, populate the five WebView2 SDK cache members using `native/usbbridge/webview2-sdk.json` and verify the package/member pins; see `native/usbbridge/HORIZON-NATIVE.md`. The audio helper builds/tests both audio codecs and copies the required payloads into `runtime/audio/`. Quiet can download a pinned SHA-256-verified LLVM-MinGW toolchain under `build/`; aicodix uses vendored headers. Do not silently change pins or substitute unsigned/modified driver binaries.

Do not launch the normal apps, driver installers, repair/setup/removal scripts, hardware smoke utilities, real camera/audio enumeration or capture, clipboard listeners/hooks, UAC, services, or certificate operations during routine implementation/validation. Those actions require an explicit task authorizing the real system operation. Offline builds and isolated fixture tests do not authorize hardware actions.

## Release review

1. Check the requested release version in both conditional branches of `src/AssemblyInfo.cs`; keep public documentation and release notes aligned. Update the Get Copy-Paste Back application version separately from component versions.
2. Run the full managed suite and any affected native checks. Record the exact command, exit status, and meaningful results. A clean PCM codec roundtrip or synthetic video/JPEG fixture is not a real Horizon throughput measurement.
3. Build the final binaries from reviewed sources. Omitting `-NoPublish` copies the standard release files to `dist/`; inspect that output. Follow [docs/RELEASING.md](docs/RELEASING.md) and use `scripts/Package-Release.ps1` for the release allowlist, archives, and checksums. Include both matching application EXEs and required notices/corresponding-source materials.
4. Review repository and release contents for private keys, camera identity/session files, saved user settings, logs, received clipboard/files, personal paths/names, credentials, build caches, and unrelated executables. Never package the active `HorizonServer/` runtime directory or a user's client folder wholesale. Do not delete a user's retained files to prepare a release.
5. Preserve component copyrights/licenses, source pins, and provenance. Quiet/libfec requires its corresponding-source/relink materials; the EXEs support explicit `--export-third-party`. No blanket license for the original Get Copy-Paste Back application is currently specified; do not add an MIT grant without the owner's instruction.
6. Keep claims bounded to evidence: three offered YUY2 camera source formats (640×480, 1280×720, 1920×1080 at 30 FPS), five ordinary video modes plus four optional color modes, one selected client audio output and all or one local server capture output. The future virtual-driver/multiple-channel diagram is unimplemented and does not establish independent Horizon channels or linear scaling. Distinguish measured single-session file throughput from calculated payload ceilings.
7. The owner has explicitly authorized completed updates to this project to be pushed and new versions/releases to be published. This standing authorization persists across user turns: after preparing and verifying the reviewed changes and artifacts, follow [docs/RELEASING.md](docs/RELEASING.md), `scripts/Package-Release.ps1`, and the tag workflow in `.github/workflows/release.yml` without asking for confirmation again. A new destructive action outside this authorization still needs approval. Do not force-push, move existing release tags, or remove published assets as part of an ordinary update.

**Publication resumed (2026-11-15):** The owner explicitly authorized the Get Copy-Paste Back rebrand, the `AlexeySmirnov74/get-copy-paste-back` repository and publication of 0.6.39 including the local 0.6.38 fixes. Follow the verified release workflow above. The 0.6.38 notes remain a historical record of that local build.

Use small, source-backed changes and report limitations directly. Preserve offline operation of packaged runtime content. Do not insert downloads, website dependencies, analytics, or licensing prompts into normal application startup.

## Color modes and audio preferences

The color checkbox filters only the server's offered modes and speed test,
not physical YUY2 camera formats or client decoder compatibility. Keep explicit
false settings authoritative, preserve ordinary defaults and keep active mode
changes from mutating a running benchmark. A changed packet size needs a fresh
transfer identity, including when color is disabled during a pending copy.

Server audio choices use stable WASAPI endpoint IDs; an empty/failed enumeration
must not replace a saved choice with the default device. Client `audio.id` keeps
the compatible endpoint/name lines and optionally a third stable identity line.
Restore explicit identity first, migrate old names only when unique and never
reuse a numeric WinMM index for a different device. A failed diagnostic or
temporarily missing device does not erase an explicit user preference.

Server `audioOutput="all_render"` selects independent capture of all active render
devices. An absent attribute adopts this default; an explicit empty attribute
preserves Windows-default routing. Never pass the sentinel to native capture,
mix different endpoints' PCM, or treat multi-output reception as extra bandwidth.
