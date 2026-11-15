# Catalog camera discovery implementation plan

**Goal:** Publish 0.6.40 with automatic discovery limited to the saved camera and the server's shared 100-name catalog, without recurring probes of unrelated webcams.

**Design approved by owner:** Try the saved exact Device ID first; otherwise consider only catalog-name matches and require the existing validated bridge control before selecting a source. Keep manual correction authoritative and persistent. Failed candidates must not be opened in endless background cycles. The user authorized implementation and a GitHub release.

**Architecture:** Compile `CameraNames.cs` into both roles. Keep camera eligibility, bounded attempts and reset conditions in `ReceiverContext.AutoCamera.cs`; preserve the existing asynchronous session/epoch guards and manual selection persistence. Keep one finite pass of the five existing capture backends so a camera that needs Media Foundation can recover from an initial DirectShow error. A name match filters candidates but is not proof of ownership.

**Tech stack:** C# 5, .NET Framework x64, Windows Forms; existing offline scripts and synthetic capture fixtures.

## Constraints and review focus

- Noncatalog built-in cameras must have zero automatic opens; a saved exact ID or explicit manual selection remains usable even with a custom noncatalog name.
- Saved Device ID takes priority over enumeration order or optional preference hints. Device names use the shared catalog's case-insensitive exact matching, not substrings.
- Retain finite DirectShow/Media Foundation compatibility fallback per candidate; exhausting it stops automatic reopening until an explicit retry/reset or process restart. Ordinary refreshes cannot reset exhaustion. Newly appearing Device IDs remain eligible.
- Probe frames do not publish clipboard data or acknowledge delivery. Stale callbacks cannot override a user's manual choice or an automatic-selection toggle.
- Existing storage, mutexes, resource keys, native versions, wire formats, transfer limits and audio routing remain compatible. No real camera/audio enumeration/capture, app launches or driver operations during validation.

## Tasks

- [x] Reproduce catalog filtering, saved priority and finite retries with failing isolated receiver fixtures; adjust existing tests to use catalog names where their intent is automatic discovery.
- [x] Share the catalog through build inputs and implement the bounded discovery policy, preserving compatibility recovery and manual override behavior.
- [x] Update English/Russian instructions and 0.6.40 notes; independently review policy, lifetime and documentation claims.
- [x] Run the complete offline build/fixture suite; package Server.exe, Client.exe and the ZIP, checking versions/resources/allowlist/checksums.
- [ ] Audit and commit the exact public source tree, push main and a fresh v0.6.40 tag, then verify both CI runs and download/check every public asset.

## Validation record

Baseline: published 0.6.39 passed the full configured suite. Before this change, discovery opened all enumerated camera names and cleared its attempted set every five seconds; the supplied logs demonstrated five opens of an unrelated built-in camera. Regression fixtures exercise eligibility and actual capture-open boundaries rather than merely inspect source text.

The isolated receiver fixture reproduced the catalog failure before implementation and passed 244 assertions after the fix. Mutation checks correctly failed when allowing a sixth unverified open, clearing unrelated exhausted IDs after successful identification, or omitting the identified source's own retry reset. A separate actual-EXE check reproduced the missing client catalog in 0.6.39. Independent source review approved eligibility at the capture boundary, retry/reset rules, compatibility fallback, and stale-callback guards. All fixture inputs are synthetic, with isolated settings and fake capture services.

Full validation: `powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/Build.ps1 -NoPublish` exited 0; all configured suites passed. `scripts/Package-Release.ps1 -Version 0.6.40 -ValidationLog build/validation-0.6.40.txt -UpdateRoot` exited 0 and verified matching application versions, content-bound build evidence, the three-file ZIP allowlist, and SHA-256 checksums. No real Horizon session or camera/audio hardware was exercised.
