# Simple README and screenshot gallery

**Goal:** Help an ordinary Horizon user understand the copy/paste problem this project solves, download the correct EXEs, and complete a first transfer.

**Requested design:** Keep English as the default entry point and provide Russian alongside it. Lead with the user's problem, ready-made downloads, a plain camera/sound picture and a short setup/copying sequence. Embed the supplied screenshots 2 and 3. Preserve all five original screenshots in `screenshots/` and link the gallery. Move technical explanations into separate English/Russian guides instead of removing them. The CryptBoard README is a reference for clarity, not application behavior or copied wording.

**Scope:** Documentation and original screenshot assets only. Keep application version 0.6.40 and its published binaries unchanged. Root README links continue to download the latest ready-made release. No app launches, real devices, clipboard hooks or driver operations.

- [x] Read both existing READMEs and the referenced CryptBoard description; inspect and copy the five supplied screenshots unchanged.
- [x] Rewrite the English/Russian entry points around download, setup, copy/wait/paste, and simple troubleshooting.
- [x] Preserve detailed behavior, measurements, build instructions and component terms in parallel technical guides.
- [x] Add accessible English/Russian SVG interaction pictures and a bilingual five-image gallery.
- [x] Check links, image paths, SVG layout, screenshot hashes, documentation claims and the unchanged application build fingerprint.
- [ ] Audit the public source selection, publish the documentation commit to main, and verify GitHub rendering/assets and CI.

Validation: both SVGs rendered and inspected without clipping. The documentation check resolved 148 local links/anchors, checked the requested inline images and gallery, verified all five original PNGs by SHA-256, and checked preserved limits/measurement tables. Independent review corrected the start-beep wording to mean acceptance for preparation and retained the installer's restart branch. Application build inputs still match the fully verified 0.6.40 fingerprint, so this is a documentation-only publication.
