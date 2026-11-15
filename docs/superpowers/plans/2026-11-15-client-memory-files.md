# Client storage and icon implementation plan

> **For agentic workers:** Use subagent-driven-development or executing-plans to implement each owned task and review its evidence.

**Goal:** Replace the green icons, put client audio DLLs in its settings store, and eliminate client received-file disk staging.

**Architecture:** Existing settings resolution supplies the native DLL cache root. A bounded OLE virtual-file object replaces only the client preparation/publication delegates. Existing server disk preparation remains independently tested.

**Tech Stack:** C# 5, .NET Framework x64, Windows Forms, Windows Shell OLE, offline PowerShell build.

**Spec:** [Client memory-files design](../specs/2026-11-15-client-memory-files-design.md).

## Global constraints

- Preserve offline runtime, native pins, file validation and wire compatibility.
- No tests against real clipboard, camera, audio or drivers.
- No deletion of existing user files or user-supplied DLLs.
- Keep English/Russian documentation consistent; publish a fresh version without moving tags.

## Review focus

- Busy clipboard: retry the same prepared object without disk writes or premature receipt.
- Repeated stream requests: independent seek positions and storage-medium ownership.
- Folder/Unicode/empty-file payloads: complete names, hierarchy, attributes and sizes.
- Unwritable profile folders: audio uses the resolved settings fallback.
- Clipboard lifetime: OLE owns the data object; no permanent application cache after clipboard replacement.

## Tasks

- [x] Reproduce current client disk staging with an isolated receiver-default regression test.
- [x] Add memory-file data object and synthetic COM negotiation/stream/validation tests; change receiver default delegates.
- [x] Move client audio storage and verify client/server separation plus packaged EXE loading.
- [x] Generate and inspect shared same-green icon at native tray sizes and validate ICO frames.
- [x] Update bilingual storage/paste documentation, in-app help and version/release notes.
- [ ] Review implementation, run full suite, package, push new version/tag, and verify published downloads.
