# Client storage and shared icon

The owner requested a replacement icon with the same green color, client audio DLLs under `%LOCALAPPDATA%/HorizonClient/Settings`, and no client `Received` directory or retained received-file copies. Implementation details are delegated to the developer; no further design choice is needed from the owner.

Use one clipboard/two-arrows icon for both applications, preserving green `#60B515` and supplying native small-tray and executable sizes. Keep its editable SVG and offline generator in the repository.

The client resolves its existing writable settings directory (Local AppData, then Roaming, then EXE-directory fallback) before loading audio. Native DLLs go in that directory's `audio-native` child. Preserve hash-addressed names, bounded extraction, relink support and cleanup of stale owned libraries. Server DLL storage remains independently configured.

For client-received files, replace disk staging with the Shell's standard `FileGroupDescriptorW`/`FileContents` OLE data object backed by bounded memory. File bytes are written only by the paste target into the destination selected by the user. Keep the client running until the paste completes. Applications accepting only filesystem `CF_HDROP` paths may not accept virtual files. Closing the client before paste loses access to its virtual-file clipboard. Windows and destination applications may independently manage their own clipboard history or temporary data; the application does not create a received-file cache.

Alternatives considered: deleting a temporary staging directory after paste still writes an unwanted intermediate copy; moving `Received` to AppData hides rather than removes it. Keeping content in memory meets the requested behavior. Server-side reverse-file staging is outside this client-specific change.

Preserve serialization/compression, SHA-256, safe paths, item/size limits, exact-transfer acknowledgements, echo suppression and clipboard-busy retries. Preparation must complete before the client announces clipboard readiness. Use fresh independently owned COM streams for repeated target requests and standard storage-medium ownership. No new runtime downloads, dependencies or driver operations. Existing received files and user-provided libraries are not deleted as part of this update.

Verification uses synthetic payloads, isolated directories, COM data-object queries and native memory streams without touching the actual system clipboard, audio devices or camera. Run the full managed suite and package checks, publish a new version through the existing GitHub workflow, and verify downloaded release assets.
