# Get Copy-Paste Back rename and release plan

**Goal:** Publish the latest fixes under the product name Get Copy-Paste Back, repository `AlexeySmirnov74/get-copy-paste-back`, and applications `Server.exe` / `Client.exe`.

**Design:** Rename public product identity, WinForms/tray/help text, output binaries, release archive, workflow and current bilingual documentation together. Keep existing storage directories, mutexes, namespace/resource keys and wire identities compatible with installed versions. Preserve third-party names, source pins and historical release notes. The owner explicitly resumed publication after the local-only 0.6.38 build; publish a distinct 0.6.39 tag after validation.

**Stack:** Existing C# 5 / .NET Framework x64 / WinForms, offline native payloads, PowerShell build/package, GitHub Actions.

## Tasks

- [x] Update actual-EXE validation for new branding/output names and demonstrate failure on old binaries.
- [x] Rename managed outputs, assembly metadata, runtime visible product names and self-launch instructions; keep legacy settings and protocols intact.
- [x] Update packaging/launch helpers/CI and current English/Russian docs, diagrams, badges and release notes for the new repository and filenames.
- [x] Independently review all changed source; run the complete offline build and synthetic fixture suite, then package/check renamed files and archive.
- [ ] Copy the reviewed changes into the main working directory, audit the exact commit allowlist, and commit without private settings/logs/runtime state.
- [ ] Rename the existing GitHub repository with history/releases preserved; update origin URL and repository description/topics. Push main and the fresh v0.6.39 tag.
- [ ] Confirm main/tag CI and release publication; download and verify all public assets, names, versions, icon resources and checksums before reporting completion.

## Review focus

- A renamed EXE must load embedded drivers/modems and invoke elevated helpers using its actual new path.
- Existing user camera/audio preferences and single-instance behavior must survive the rename.
- The ZIP and manifest must list Server.exe, Client.exe and the new archive name; no active runtime directories or private logs.
- GitHub badge/download/release URLs must use the new slug, while historical tags/assets remain intact.
- EN/RU forms, tray version lines and concise instructions must fit existing size/DPI validation.
