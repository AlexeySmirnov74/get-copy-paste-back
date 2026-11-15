# Publishing Get Copy-Paste Back updates

The project owner has requested that completed updates be published to [AlexeySmirnov74/get-copy-paste-back](https://github.com/AlexeySmirnov74/get-copy-paste-back) with a new release. Publication of the rebrand and the fixes prepared locally in 0.6.38 is authorized for 0.6.39. Use the repository's existing GitHub authentication; never place tokens in source, logs or release assets.

1. Update **both** server/client assembly versions in `src/AssemblyInfo.cs` to `X.Y.Z.0` and add bilingual `docs/releases/X.Y.Z.md` notes.
2. Review the diff, update the English/Russian documentation when behavior changes, and keep user settings/logs/received files/private keys out of Git.
3. Run `powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Package-Release.ps1`. This compiles both EXEs, executes the full synthetic suite and verifies a three-file ZIP plus SHA-256 checksums. It never installs a camera or opens real audio/video devices.
4. Commit the reviewed changes. Confirm a clean tracked working tree, push `main`, then create and push the matching annotated tag:

   ```powershell
   git tag -a vX.Y.Z -m "Get Copy-Paste Back X.Y.Z"
   git push origin main
   git push origin vX.Y.Z
   ```

5. The Windows GitHub Actions workflow rebuilds/tests the exact tag and publishes `Server.exe`, `Client.exe`, `Get-Copy-Paste-Back-X.Y.Z.zip` and `SHA256SUMS.txt`. Versioned local packages are under `dist/Get-Copy-Paste-Back-X.Y.Z/`. No extra personal access token is required by the workflow. Check that the job succeeds and download/verify the published checksums before reporting completion.

`Package-Release.ps1 -Version X.Y.Z -ValidationLog .\build\validation-X.Y.Z.txt` can reuse a completed full validation whose recorded SHA-256 matches all code, tests, scripts, embedded runtime/default/vendor inputs and both binaries. Changed inputs, mismatched binaries and output from `-SkipTests` are rejected. `-UpdateRoot` also refreshes the root `dist` EXEs if neither app is running; versioned output is always available.

Do not move an existing release tag or force-push history. If a release already exists, inspect it before changing its assets: rebuilding identical source can change executable bytes. Prefer a new version for code changes. If GitHub Actions is unavailable, upload the locally verified manifest's four assets using `gh release create --verify-tag --notes-file docs/releases/X.Y.Z.md`; verify the remote tag points to the tested commit first.

CI uses pinned `actions/checkout` and a Windows 2025 runner. Native DLLs, backend and driver installer are pinned binary inputs with corresponding notices/source. Rebuilding those dependencies is a separate audited change; the ordinary application build needs no network downloads.

The executable rename preserves the existing `HorizonServer/` runtime directory and `HorizonClient/Settings` locations so upgrades find saved settings. C# namespaces, embedded-resource identifiers, helper filenames and native component versions retain their compatibility names. Do not package a user's runtime/settings folders or rewrite historical release notes to match the new brand.
