# Local audio default and camera readability implementation plan

**Goal:** Receive return audio from all active playback devices by default, preserve explicit choices, and keep the unavailable camera name readable.

**Architecture:** Keep Windows-default (`null`) and explicit endpoint routing unchanged. Add a distinct persisted all-outputs selection; enumerate active render endpoints on the bridge worker and feed each into its existing independent decoder and shared duplicate-safe assembler. Clear native camera edit selection at the lifecycle boundary identified by the focused UI fixture.

**Tech stack:** C# 5, .NET Framework x64, Windows Forms, existing offline build and isolated fixtures.

**Authorization:** The user's request specifies these changes. This is a local-only update: no push, tag, GitHub release, real device capture, camera operations or ordinary application launch during validation.

## Decisions and constraints

- Fresh settings and older settings with no audio attribute use all outputs. Existing explicit endpoint IDs remain authoritative; an explicitly selected Windows-default option is saved as an empty `audioOutput` attribute so it survives restart.
- The all-outputs mode opens one decoder/capture per unique active endpoint, keeps PCM streams separate and retains existing transfer/session/receipt deduplication. One failed route must not disable healthy routes.
- Keep both English and Russian UI/documentation consistent. Preserve camera name editing when available.
- Build a distinct local 0.6.38 pair without changing the published 0.6.37 artifacts or copying user logs/settings into packages.

## Tasks

- [x] Audit both supplied logs, correlate transfer completion and audio receipts, distinguish recoverable fallback events from unhandled errors.
- [x] Add failing selection/XML fixtures for fresh all-output default, legacy explicit device, explicit Windows default and all-output persistence; implement the minimal settings/menu changes.
- [x] Add failing synthetic bridge fixtures for unique all-output routes, duplicate packets, independent failure, empty/failed enumeration and disposal; implement all-output routing on the existing worker.
- [x] Reproduce the camera-name selection through the native control lifecycle; fix it and verify hidden-form editability/focus/language scenarios.
- [x] Review source changes; run focused tests, then `powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/Build.ps1 -NoPublish`.
- [x] Package local 0.6.38 using the completed validation log, verify EXE versions, ZIP allowlist/checksums and icon resources. Report the local download path and log findings; publish nothing.

Validation: the full offline build completed with exit 0 and all configured suites passed. Source fingerprint: `E6C92E42C49F8A5E5FD846689C7294451CF3EC63AE0604AD1FDE9BFEC3FA12E1`. Independent source review approved. The local ZIP contains only both matching application EXEs and START-HERE.txt; no user configuration, logs or runtime folders were packaged. Source changes and binaries were copied into the main working directory without commits, pushes or tags.
