# Changelog

### Runtime authorization confirmed; original correspondence remains private

- Recorded the maintainer's confirmation of native MSFS 2024 SimConnect.dll redistribution authorization, with no additional conditions reported. The original correspondence is retained privately and is not requested or published.
- Added `New-RuntimeApproval.ps1` to bind the public attestation to the real SDK DLL's SHA-256, public maintainer label and UTC record timestamp without manual JSON editing or copying correspondence.
- Version 2 approval records use a public authorization/conditions summary. Packaging strips private excerpts, source details, private references and extra fields; final validation rejects reintroduced private fields. Email archives are refused in distribution inputs.
- Preserved actual Windows loading/export checks, official SDK compilation/provenance and minimum real simulator acceptance. No complete release ZIP, final checksum or new Windows/MSFS pass is claimed from the authorization confirmation.

### Public alpha release preparation — local Windows build and manual upload

- Authorized public distribution through the existing repository's Releases page; removed the private destination/token requirement. A self-hosted runner is optional and is not needed for the first release.
- Added a Windows candidate builder reusing the official SDK/compiler, native runtime checker, app build and automated recovery/package checks. Full builds omit probe assets and debugging symbols; no source, test launchers or private data belongs in the final archive.
- Added candidate testing followed by finalization without rebuilding. Acceptance binds the exact clean source commit and operational payload; altered builds and stale evidence are refused. SDK hashes/ABI proof and runtime permission/loading remain mandatory.
- Added the required `AeroScience-v0.1.1-alpha-win-x64.zip` name, checksum, concise tester README/defaults and generated release notes. Version metadata supports future alpha/beta/stable releases from the shared version property.
- Defined seven minimum real Windows/MSFS alpha checks; exhaustive aircraft/Mach/storage/iPad/VR checks may remain explicitly pending for this prerelease. Stable acceptance retains extended gates.
- Added an optional two-stage public Actions flow using `github.token` with `contents: write` only in publication, plus retry-safe tag/asset checks. Conflicting existing releases/tags/assets are never overwritten; ordinary public downloads are checked after publication.
- Added official Visual C++ prerequisite guidance in Settings and documented the unresolved SimConnect redistribution designation in Microsoft's public SDK EULA. No proprietary runtime, full release archive or hardware pass is fabricated; publication remains blocked on that permission and the new gaming-PC build/tests.

## 0.1.1-alpha — Live Flight Recording Alpha (implementation; hardware acceptance pending)

- Added one Windows entry point, `AeroScience.exe`, hosting local services and opening the dashboard. Native settings manage connections, toolbar installation, storage and feedback without tester terminal commands. Offline mission history works without MSFS or internet; live recording requires fresh aircraft data.
- Removed production synthetic telemetry and demo launchers. Test fixtures remain separate; historical simulated missions retain their source labels.
- Added actual `AIRSPEED MACH` telemetry to the native definition, shared model, dashboard, toolbar, SQLite sample JSON and complete JSON exports. Historical samples may omit Mach; invalid Mach does not discard otherwise valid data. Expanded native-layout checks preserve the previous SDK corrections.
- Reproduced sample loss caused by jitter around the old interval threshold and replaced it with scheduled real-sample selection. Added configured/observed/mission-average cadence and gap diagnostics. Sustained live 1 Hz acceptance remains pending.
- Added independent Mission Database and Scientific Data Library folders with native browsing, open/default actions, usage/free-space information and the requested large-data warning. Science storage contains an identity manifest and empty organizational folders; no instruments or artificial observations were added.
- Added confirmed, exclusive migrations using SQLite backup or verified library copies, capacity/permission/conflict checks, durable recovery journals and retained originals. Active recording blocks changes. Unavailable selected stores never silently become empty replacements. Dataset IDs and library-relative references survive library moves.
- Added schema 3 with verified pre-upgrade SQLite backups, dataset references and compatibility with v0.1.0 modes/history. Export includes all recorded samples, timestamps, source labels, modes, events and dataset references.
- Preserved the native toolbar movement/resize bootstrap and single responsive Mission/Payload interface. Removed the dashboard marketing slogan and shortened user guidance.
- Added provenance/checksum gates and a manually dispatched private alpha release workflow. Public CI publishes no binaries. Official SDK output, permitted native runtime delivery and genuine acceptance evidence are required for a complete alpha archive.
- Added regression coverage for offline startup/history/export, Mach, migration/recovery, storage independence, reference integrity, recording cadence and toolbar installation. See [validation results](docs/validation-results.md) for measured results and remaining Windows/MSFS/device checks.

## 0.1.0 — development foundation (not yet a verified Community release)

### Native storage bootstrap correction — moving/resizing passed

- Actual Coherent inspection found both native movement/resize handlers absent. Manually running the installed SDK storage-ready initializer created both, restored full-window sizing, and restored moving/resizing in the user's probe test.
- Added the missing simulator-provided `/JS/dataStorage.js` import to the full toolbar and probe before native template initialization. Native handlers remain owned by the SDK; no manual initializer call, custom movement code, SDK implementation or engine change is shipped.
- Added regression coverage requiring exactly one storage bootstrap in both pages, after common.js and before the native frame import. The user subsequently confirms dragging/resizing after the `03b06f0` full-toolbar rebuild instructions. Exhaustive controls/edge coverage, startup repeatability and full mission/device acceptance remain pending.
- Added a v0.1.1 planning handoff prioritizing actual MSFS aircraft telemetry and persisted flight missions, supported by remaining acceptance tests, installable friends-only alpha assets and genuine SDK/release automation. No v0.1.1 features or automation are implemented by that report.
- Extended that planning brief with explicit v0.1.1 requirements for one user-facing executable, no required PowerShell/terminal commands, graphical first-run setup and removal of production data simulation. The current v0.1.0 app is unchanged by those future requirements.
- Recorded initial actual-aircraft telemetry success: the user's live dashboard shows connected `T-38N WSSP Late` readings and an active Universal recording with 16 samples. Completed live-history inspection and restart persistence still require testing; the v0.1.1 handoff now builds on this observed working integration.

### Native window investigation — unresolved v0.1.0 blocker

- Recorded the failed `9a1946d` desktop retest: normal title-bar dragging/resizing still fail, and dragging the resize-looking left edge moves the entire window. Latest scrolling is unconfirmed; synthetic telemetry still arrives.
- Removed Compact VR entirely: checkbox, switching logic, compact-only CSS and obsolete assertions. Mission/Payload remain in one responsive interface.
- Added a separate `-WindowProbe` SDK build with native imports/static content and no mission code or application CSS. Preserved production native configuration pending actual handler/geometry evidence; no speculative drag/resize correction is claimed.
- Corrected missing button script/template imports in the probe after actual Coherent inspection showed `buttonTemplateLoaded: false` and `headerTemplateLoaded: true`. Added a native-bootstrap parity check; corrected probe controls and native resizing require a fresh simulator retest.
- Added an opt-in passive Coherent capture for native element bounds, event paths, hit targets and errors, plus precise Windows build/replacement and desktop debugging/checklist instructions. Existing read-only CommBus diagnostics and companion are preserved.
- Replaced headset interaction acceptance with performance-only VR measurements and `vr_performance` release evidence. Diagnostic variants cannot pass the full-toolbar release gate.
- Updated browser/regression checks. Official Windows SDK builds, native input/call-stack diagnosis and hardware acceptance remain unverified in this Linux cloud. No replacement engine, scientific feature, replay or release automation was added.

### Previous toolbar interaction attempt (`9a1946d`)

- Changed registration geometry/resize hints to attributes and introduced overridable frame sizing and bounded content wrappers/scrolling. Browser tests passed but did not establish native behavior; the subsequent desktop retest failed as recorded above.
- The temporary header layout control is removed in the current revision. Earlier headset interaction acceptance was superseded by performance-only guidance.

### Windows installation-readiness review

- Added a gaming-PC workbook for preview downloads/extraction, simulated-mode checks, official SDK/runtime checks, toolbar POC/full builds, live aircraft/mission recording, VR performance, iPad LAN and GitHub backup.
- Fixed Windows separator normalization in SDK build receipts. Preserved compiler/package-tool logs and native failure codes; added explicit SDK tool/header overrides and stronger installed-header ABI checks.
- Corrected the first reported Windows SDK contract failure: verify CommBus member/payload offsets instead of treating the 32-byte fixed header as the size of the variable-length structure. Simulator acceptance remains pending.
- Accepted SDK-generated three-component minimum game versions (observed `1.9.18`) alongside four-component versions. Missing/malformed versions remain rejected, and diagnostics now include the actual value. Added packaging regression tests.
- Matched SDK layout/required-asset paths without regard to letter case while preserving original filenames and hashes for build receipts. Missing files, incorrect sizes, case-only duplicates and colliding filenames remain rejected. Added regression coverage for lowercase layouts and mixed-case/lowercase output files.
- Recorded the first user-reported successful Windows SDK proof-of-concept build through structural validation and script completion, followed by MSFS screenshots and user confirmation of diagnostic-panel registration, desktop rendering and recurring companion CommBus replies. Long-duration/lifecycle transport, mission controls, the installed folder path, full build and live telemetry/device acceptance remain pending. Added a testing handoff report for further development.
- Made the simulated launcher explicitly standalone (bridge/LAN disabled), retained launcher exit codes, and improved DLL-present/missing-dependency diagnostics.
- Added a native runtime architecture/export checker and a local feedback ZIP collector that excludes databases, SDK binaries and credential dumps.
- Prevented companion preview archives from accidentally bundling SimConnect runtimes, including case variants/nested files. No GitHub Release automation or unrelated science features added.
- All Windows/MSFS/Community/VR/physical-iPad acceptance remains pending until performed on the actual hardware.

### Initial foundation

- Added .NET 10 engine, native SimConnect telemetry provider with lifecycle/reconnect handling, and explicit synthetic telemetry mode.
- Added transactional SQLite mission recording, timestamped events, position/altitude summaries, paginated telemetry retrieval, history/detail views and recovery of interrupted missions.
- Added a responsive local dashboard with live SSE synchronization, mission controls, altitude profile and diagnostics.
- Added Universal/Realistic selection. Universal is the default; both modes persist across restarts, appear in shared web/toolbar state and history, and accept compatible aircraft without a whitelist. Schema 1 → 2 safely defaults legacy missions to Universal.
- Added graceful unknown-title and unavailable-AGL fallbacks. NASA aircraft are examples of future research presets, not required aircraft.
- Added private-LAN pairing, expiring sessions, same-origin/CSRF/Host protections, bounded logs and exclusive database ownership.
- Added SDK toolbar sources, a read-only registration/CommBus probe, mission controls and mode selection.
- Added self-contained Windows publishing, official-SDK build/ABI checks, package validation and guarded release assembly.
- Advanced Realistic Mode instrument restrictions, calibration, sensors, mapping, scientific observations and full replay remain future features. Realistic and Universal record identically in this version.
- Live Windows/MSFS, SDK package compilation/installation, Community2024, VR performance and physical iPad Safari checks remain pending. No full release archive is marked installation-ready.
