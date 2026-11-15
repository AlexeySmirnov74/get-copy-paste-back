# Optional four-color modes and 0.6.37 release

User request: keep the successful local color implementation, inspect both
provided logs, make its four modes optional through a persisted server setting,
verify matching EXE/tray icons, remember explicit audio devices, improve failed
speed-test guidance and both README entry points, then integrate into main.
The prior no-publication restriction was for the experiment; this request and
the standing release instructions authorize the reviewed main update/release.

Architecture: keep wire IDs 12-15 and decoding available on the client. The
server alone filters its dropdown and benchmark list using `colorModesEnabled`.
It does not alter the three YUY2 camera capabilities or require a new driver.
Use the existing C# 5, x64 .NET Framework/WinForms build and pinned resources.

Ruling: fresh and ordinary legacy settings default to five monochrome modes.
Explicitly enabling the setting exposes nine modes. A legacy experiment setting
with a saved color profile and no flag migrates to enabled; an explicit false
wins. Turning color off normalizes a selected color profile to1080p robust and
restarts any pending forward transfer with a new transfer ID. Disable mode
changes during a speed test; retain completed test results for comparison.

- [x] Inspect current server and client logs; distinguish normal candidate
  rejection/cancelled superseded feedback from failures. Both runs (10/100 KiB)
  completed all nine modes, with 18 verified completions and no error events.
- [x] Root: test then implement the persisted color checkbox, five/nine catalog,
  safe active-transfer switch, migration and bilingual UI/layout behavior.
- [x] Root: add bilingual audio-output guidance to an entirely failed speed
  test and unconfirmed-resolution warning, without changing successful rows.
- [x] Audio implementer: verify/fix explicit client/server audio persistence
  through restart/reordering/missing/ambiguous devices using injected endpoints.
- [x] Docs/icon implementer: verify native PE and embedded tray icon frames,
  add live GitHub badges, ready-built release download entry points and optional
  color instructions in English/Russian. No misleading license/CI badges.
- [x] Review integration, remove obsolete experiment-only packaging constraints,
  update version/release notes/AGENTS, run full offline build/fixture suite.
- [x] Package only reviewed binaries/instructions/checksums; preserve user state.
- [ ] Commit/review/merge main, publish source and immutable new release tag,
  check GitHub Actions and verify downloaded release asset hashes.

Validation covers settings migration, safe toggling without changing physical
formats, preservation of control decoding and clipboard integrity, valid audio
device selection, ambiguous/missing routes, failed-test guidance in both
languages, hidden form bounds at100/150% and icon resource consistency. Never
launch normal apps, enumerate real devices, modify drivers or access a real
clipboard as part of these checks. Actual user logs stay outside Git/assets.

Final pre-publication evidence: all configured suites passed via
`scripts/Build.ps1 -NoPublish`, exit 0. Validated build-input SHA-256:
`AB0650F3140C6FF834ED60CDEF1700532CF3BD2A3B2D4F6B15B4EFDA7FF7B012`.
`scripts/Package-Release.ps1 -Version 0.6.37 -ValidationLog <completed log>`
verified both final binaries and the three-member ZIP. Source audit selected
1908 files with no recognized credential or user-state markers. Independent
reviews approved color, audio, icons, documentation and the complete branch.
