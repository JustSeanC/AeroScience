# Verification and remaining acceptance

Machine: Linux x64 cloud container, .NET SDK 10.0.402 / runtime 10.0.13, Node 24, existing Chromium. Automated results concern this environment, not Windows or simulator execution.

## Automated checks

- .NET unit/in-process API tests: creation/completion/conflicts; full telemetry persistence; interrupted recovery; invalid data/names; sampling; aircraft-change events; two authenticated displays; SSE content; CSRF/Origin/Host checks; pairing rate limits; LAN address checks; mode selection/defaults/persistence/legacy migration; toolbar command deduplication; bounded native packet decoding; unknown-aircraft/hover fallback.
- Chromium browser tests: real engine recording/history, two-display synchronization, literal rendering of an HTML-like mission name, reload persistence, display independence, 744 px and 375 px responsive layouts, and a mocked SDK toolbar UI/protocol test. The mocked test does **not** validate Coherent or MSFS.
- Python release checks: reject source-only packages, unsafe paths, incorrect layout sizes, POC receipts, pending release acceptance and accidental archive overwrite.
- Actual-process smoke test: record samples in Realistic mode, refuse a competing engine before recovery, kill the process, restart from the same SQLite files, preserve interrupted samples/mode, complete a default Universal mission, and retain completion across another restart.
- Self-contained `win-x64` cross-publish: inspect the resulting PE target and included hosted web/runtime files. Cross-publishing is not Windows execution.

Exact current counts/results are recorded in `validation-results.md` after the final run. Generated TRX, Playwright JSON and local screenshots are retained under ignored test/artifact directories.

## Reported gaming-PC build

On 2026-10-16 the user's Windows console output confirmed successful SDK contract compilation, official package validation, nine-file Community structural validation, and completion of the proof-of-concept build script through its receipt-writing stage. See [the evidence record](validation-results.md#successful-gaming-pc-proof-of-concept-build). The generated package is on that PC; clean-machine installation, live telemetry and device acceptance remain separate pending checks.

The user's subsequent [MSFS screenshot](validation-results.md#proof-of-concept-panel-opened-in-msfs) confirms registration and visible desktop rendering of the diagnostic panel. A [later screenshot](validation-results.md#first-visible-commbus-reply) shows a companion JSON reply, and the user confirms recurring request-ID updates about once per second. Full reply inspection, long-duration/reconnection behavior, mission commands, live aircraft telemetry and device checks remain pending. See the [testing handoff](v0.1.0-testing-handoff.md) for the next development conversation.

## Windows, SDK, simulator and device checks

1. Launch the published companion on a clean Windows x64 machine without a development SDK. Verify offline synthetic mode, logs, mission history, both modes and upgrade preservation. Check that application data is outside the installation and retained on uninstall.
2. Installed-header ABI contract compilation is user-reported passed for the first POC build. Still confirm native DLL version, redistribution terms, data definitions/units/signs, Sim state, pause/loading behavior and lifecycle callbacks.
3. In actual MSFS, load default, freeware/payware and rotary-wing aircraft. Verify TITLE/fallback, position, MSL/AGL, IAS/TAS/groundspeed, true heading/track, attitude, vertical speed and zero-speed hover. Unknown aircraft in Realistic mode must remain recordable; no advanced restrictions are present.
4. Record through pause/resume, aircraft changes, flight restarts, temporary simulator disconnect and simulator exit/restart. Ensure gaps are explicit and no old frames are recorded. Inspect the persisted event timeline.
5. The toolbar POC package build and structural validation are user-reported passed; screenshots confirm panel registration, visible desktop rendering and a CommBus reply, and the user confirms recurring updates. Retain its generated manifest/layout/SPB and receipt, then verify long-duration replies, icon, floating/resizing and connection lifecycle. Restart MSFS as needed after rebuilds.
6. Validate Community2024 versus legacy Community behavior and actual Store/Steam package locations. Record the working method; do not assume or install duplicate copies.
7. Build the full toolbar and create/end missions from it and the browser in both modes. Confirm identical mission IDs/modes, retained state after closing/reopening, reconnects and no recording stop when a panel is closed. Test that halted JavaScript causes no unsolicited reply backlog.
8. In Quest 3 PCVR, verify toolbar appearance, mouse/controller activation, native input focus, name entry, readable metric text, compact layout, resize/position, low obstruction and desktop↔VR transitions.
9. On a physical iPad mini 6 with Safari, verify pairing on a trusted private network, incorrect-code rejection, source/mode labels, telemetry, mission controls/history, portrait/landscape layouts and session expiry/re-pairing. Verify localhost-only mode refuses LAN connections and the Private firewall rules are sufficient. Test Edge/Chrome on Windows too.
10. Assemble the real full release only after these prerequisites pass. Inspect/extract it on another PC and verify the exact files and installation instructions. Signatures/one-click installer remain future work; do not mark the current companion-only preview as the full Community release.

## Required v0.1.0 completion before the v0.1.1 closed alpha

1. Validate and harden native SimConnect lifecycle/telemetry across real default and third-party fixed/rotary-wing aircraft.
2. Complete official-SDK toolbar packaging, CommBus/Coherent and Quest 3 testing, including Community2024 compatibility.
3. Validate clean Windows distribution, permitted runtime bundling and physical iPad LAN pairing, then produce the complete release archive.

Advanced scientific sensors and restrictions follow those reliability/distribution tasks, rather than displacing them.

Follow the [Windows testing workbook](windows-testing.md) and retain actual results before planning friends-only v0.1.1 distribution or automated GitHub Releases. Those release features have not been added during this review.
