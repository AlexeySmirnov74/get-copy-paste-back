# Third-party materials

Get Copy-Paste Back includes native components, an unchanged signed driver installer, and a C# port of MP4Modem. Their licenses apply to their respective material. This repository does not specify a project-wide license for the original Get Copy-Paste Back application. Preserve each component's copyright, full license, provenance and source pins.

| Material | License and source records |
| --- | --- |
| USBBridge camera backend, Horizon fork 0.2.21 | [MIT license](../runtime/USBBridge-LICENSE.txt), [binary provenance](../runtime/PROVENANCE.txt), [corresponding source](../native/usbbridge/) |
| USBIP-WIN2 0.9.8.1 signed installer | [BSD-2-Clause](../runtime/licenses/drivers/usbip-win2/LICENSE.txt), [upstream release and source](https://github.com/vadimgrn/usbip-win2/releases/tag/v.0.9.8.1); SHA-256 `38CAD6D4432B52D5BB9409D9AD03B72FDFFC4ADA4CD3A48FBECA1A2752A8518A` |
| Quiet and dependencies | [Pins](../third_party/quiet/SOURCES.json), [licenses, changes and rebuild instructions](../third_party/quiet/README.txt); Quiet BSD-3-Clause, liquid-dsp/Jansson MIT, libfec LGPL-2.1; retained LLVM/MinGW-w64 notices |
| aicodix modem, dsp and code | 0BSD; [provenance](../third_party/aicodix/PROVENANCE.json), [source hashes](../third_party/aicodix/UPSTREAM-FILES.json), [component notes](../third_party/aicodix/README.txt) |
| MP4Modem V2 C# port | Apache-2.0; [original license](../third_party/mp4modem/upstream/LICENSE), [integration notice and pinned upstream](../third_party/mp4modem/README.md) |
| WebView2 native loader | [Microsoft BSD-3-Clause license and notices](../runtime/licenses/webview2-sdk/), [SDK package/member hashes](../native/usbbridge/webview2-sdk.json); the separate WebView2 Runtime has its own terms |
| Rust dependencies | [Cargo.lock](../native/usbbridge/Cargo.lock), [retained license texts](../runtime/licenses/), [version/license inventory](../runtime/THIRD-PARTY.txt) |

The driver installer retains its original Microsoft Visual C++ Redistributable and terms. It is embedded in `Server.exe` for explicit offline setup. The managed build uses the existing native payloads; it does not download or rebuild them.

Both EXEs embed the audio licenses, Quiet corresponding source and relink archives, and MP4Modem license/integration notice. `Server.exe` also embeds `ThirdPartyNotices.zip` with the backend notices and retained license tree. Export these materials explicitly to an absolute directory:

```powershell
.\Client.exe --export-third-party C:\GetCopyPasteBackMaterials\Client
.\Server.exe --export-third-party C:\GetCopyPasteBackMaterials\Server
```

Normal startup does not export these notice/source archives. Keep `runtime/audio/quiet-source.zip` and `runtime/audio/quiet-relink.zip` with a source distribution and keep them embedded when distributing the EXEs. The first archive contains matching upstream source, local adapter sources and build scripts. The second contains matching objects/libraries, licenses and `Relink-Quiet.ps1`, which accepts a replacement `libfec.a`. The extracted Quiet DLL is replaceable; the application's loader does not overwrite an existing replacement. See the [Quiet component instructions](../third_party/quiet/README.txt) and the application README for the replacement procedure.

The full inherited license tree also preserves usbipd-win/VirtualBox and Unity Capture notices. The Get Copy-Paste Back package does not include those components' binaries or claim a separate driver-sources download. Their retained notices and original source locations remain available in [runtime/THIRD-PARTY.txt](../runtime/THIRD-PARTY.txt). UsbDk is an optional native source adapter and is not bundled.

The application was previously named Horizon-Enable-CopyPaste. Legacy component labels, source filenames, C# namespaces and embedded-resource identifiers remain part of the compatibility and provenance record. The public product rename does not rename upstream components or change their licenses.

No active camera configuration, local authentication key, saved user settings, received clipboard content, diagnostic logs, development certificates or native build caches belong in a public source snapshot or release package.
