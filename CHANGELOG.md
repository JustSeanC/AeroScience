# Changelog

## 0.1.0 — development foundation (not yet a verified Community release)

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
