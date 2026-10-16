# Validation results — 2026-10-16

Initial cloud validation was performed on Linux. Separately identified gaming-PC results below come from the user's supplied Windows logs. Build results do not establish simulator loading, companion execution, physical-device or published-environment acceptance.

| Check | Outcome | Scope |
| --- | --- | --- |
| `bash scripts/setup-cloud.sh` | Passed | Pinned SDK activation, frozen NuGet/npm restore, Release build and static JS check; no source/lockfile edits |
| `dotnet test AeroScience.sln -c Release --no-restore` | **23 passed, 0 failed, 0 skipped** | Missions, telemetry, SQLite, mode selection/defaults/migration/restart, API/SSE, security, shared toolbar protocol and packet handling |
| `npm run test:web` with existing Chromium | **5 passed, 0 failed, 0 skipped** | Live web workflow, multiple displays, phone/tablet layouts, display-independent recording; toolbar test mocks SDK globals |
| Python packaging unit tests | **4 passed, 0 failed** | Invalid source packages/layouts/provenance/acceptance and ZIP overwrite protection |
| `scripts/build/smoke_recovery.py` | Passed | Real process killed/restarted; at least 2 samples and Realistic mode retained; Universal default/completion retained; competing owner refused |
| Self-contained `win-x64` publish | Passed | Native x64 PE apphost, engine/runtime/database libraries and hosted web assets produced |
| Companion preview ZIP | Passed | ZIP CRC/one-root inspection, x64 publish validation; no database/log files, Community package or proprietary SimConnect DLL |
| NuGet transitive vulnerability report | No known vulnerabilities reported | Current configured official NuGet sources at validation time |
| npm production audit | No known vulnerabilities reported | Application has no production npm dependencies |
| Toolbar XML / JS | Syntax checks passed only | Not official SDK compilation, package validation or Coherent rendering |

The .NET SDK bootstrap archive was verified against Microsoft's published SHA-512. The initial vulnerable native SQLite dependency was upgraded; auditing remains enabled.

Generated evidence: `tests/AeroScience.Tests/TestResults/foundation.trx`, `artifacts/web-results.json`, `artifacts/dashboard-desktop.png`, `artifacts/dashboard-tablet.png`, the preview ZIP and SHA-256 sidecar. These are ignored development artifacts, not source commits. The ZIP contains only published companion files and user documentation.

**At the initial Linux review, unrun/pending:** Windows launch/clean-machine installation; official SDK ABI compilation and toolbar package build; actual Community/Community2024 installation on Store/Steam; real SimConnect flights/lifecycle/units; real CommBus/Coherent panel behavior; Quest 3 PCVR; physical iPad Safari/LAN; full release acceptance. PowerShell/SDK scripts and CI configuration were authored but not executed on their target Windows/GitHub runners here. See the later gaming-PC reports below and `verification.md` for current status.

The official SDK documentation URL supplied by the user returned a proxy access denial. A custom domain addition plus tested install/start instructions were saved to the cloud environment draft. Saving did not apply that network change, publish a snapshot or validate restoration in a new task.

## Windows installation-readiness review

- Re-ran the .NET suite after loader-diagnostic changes: **23 passed, 0 failed, 0 skipped**, recorded in `windows-readiness.trx`.
- Python discovery with PowerShell 7 available: **10 passed** (5 packaging tests plus 5 helper checks). Checks cover parsed PowerShell scripts, selected feedback/log collection with database/SDK exclusion, PE rejection/metadata-only boundaries, retained compiler-failure logs without a receipt, and runtime-free companion preview inputs.
- PowerShell 7.6.0 was installed on Linux from Microsoft's release archive and verified against its SHA-256 manifest. This enables syntax and portable/helper-mock checks; it does **not** establish Windows PowerShell 5.1 execution, real MSVC compilation or SDK behavior.
- The Windows receipt separator error was fixed; SDK header paths/tool overrides, compiler/package logs, runtime-check reports, clean simulated launcher defaults and feedback helpers were added. No SDK DLL was downloaded or redistributed.
- Windows launcher execution, real `cl.exe`/installed SDK contract, actual native `SimConnect.dll` loading/exports and all simulator/VR/iPad checks remain **pending on the gaming PC**. See `windows-testing.md`.

## First reported Windows SDK build

- The user's Windows compiler reached `sdk-contract.cpp` and reported `C2607` at the assertion `sizeof(SIMCONNECT_RECV_COMM_BUS) == 32`. This is a failed build, not SDK/package acceptance.
- Official Microsoft documentation was accessible during troubleshooting: [SIMCONNECT_RECV_COMM_BUS](https://docs.flightsimulator.com/msfs2024/html/6_Programming_APIs/SimConnect/API_Reference/Structures_And_Enumerations/SIMCONNECT_RECV_COMM_BUS.htm) inherits the [list header](https://docs.flightsimulator.com/msfs2024/html/6_Programming_APIs/SimConnect/API_Reference/Structures_And_Enumerations/SIMCONNECT_RECV_LIST_TEMPLATE.htm), adds `uEventID`, then a variable-length `rgData` string. The original check confused the fixed header/payload offset with the complete declared struct size.
- Replaced that size assumption with checks for the actual fragment, subscription and payload offsets used by the decoder; also asserted the telemetry payload offset. No runtime decoder or companion binary changed. Recompilation against the user's installed SDK and simulator testing remain pending.
- Re-ran the five portable PowerShell helper tests: **5 passed**, including compiler-failure log retention and refusal to issue a successful build receipt on failure. These do not compile against the Windows SDK.

## First reported SDK package output

- After the contract fix, the user's screenshot shows MSVC compilation continuing into the official package builder, the panel SPB compiling, `Validation SUCCESS`, and zero failed SDK build tasks. The log identifies game version `1.9.18.0` and package validator `1.0.7.0`; these are not an identified SDK installer version.
- The user's SDK-generated manifest reports `builder: "Microsoft Flight Simulator 2024"`, package version `0.1.0`, no dependencies, and `minimum_game_version: "1.9.18"`. Our post-build checker incorrectly demanded four components and prevented the build receipt from being written.
- Corrected that checker to accept three or four numeric components while retaining missing/malformed-version rejection, layout/asset checks, and the separate release acceptance requirements. The SDK-generated manifest is not modified. Added regression coverage for both accepted forms and invalid/missing values.
- Packaging regression suite after the correction: **7 passed**, including the reproduced three-component rejection fixed by this change. Test assets are structural fixtures, not official SDK output or simulator evidence.
- This evidence establishes user-reported SDK contract compilation and Microsoft package validation. Our complete script/receipt, installation, panel loading/transport, live telemetry, VR and iPad acceptance still need their respective checks.

## Reported SDK layout casing

- The next user-supplied build log again reports Microsoft `Validation SUCCESS`, followed by our checker rejecting lowercase `html_ui/icons/toolbar/icon_aeroscience.svg`.
- The supplied layout and file listing confirm seven lowercase layout paths with corresponding mixed-case physical names, including `ICON_AEROSCIENCE.svg`, `AeroScience.html`, and `InGamePanels/aeroscience_panel.spb`. The package contains nine files in `artifacts/toolbar-poc/output/Packages/aeroscience-toolbar`.
- Corrected only package path matching to be case-insensitive. Original names and bytes remain intact for SDK build receipt hashes. Missing files, incorrect sizes, layout self-reference, case-only duplicate entries and case-colliding physical files remain rejected. Applied the same matching to required assets and their reads to avoid a subsequent false rejection after layout validation.
- Python regression discovery with PowerShell available: **18 passed** (13 packaging checks and 5 portable helper checks). Tests reproduce lowercase layout entries against mixed-case files and lowercase output assets, and verify rejection of missing/incorrect/duplicate paths. The SDK-provided HTML/CSS/JS/icon sizes in the user's layout also match the checked-in sources.
- Full script/receipt completion, package installation and simulator/device behavior still require the gaming-PC checks; this correction does not establish them.

## Successful gaming-PC proof-of-concept build

- The user's subsequent complete PowerShell output confirms `sdk-contract.cpp` compilation, official package-tool `Validation SUCCESS` with zero failed tasks, `Community structural validation passed: 9 files`, and the final `SDK-built output` message. The proof-of-concept build script completed its receipt-writing stage without a reported error.
- Output: `C:\Dev\AeroScience\artifacts\toolbar-poc\output\Packages\aeroscience-toolbar`. SDK root shown in the log: `C:\MSFS 2024 SDK`; game `1.9.18.0`; package validator `1.0.7.0`. The exact SDK installer version remains unreported.
- Preserve `artifacts/toolbar-poc/sdk-contract.log`, `build.log` and `sdk-build.json` on that PC with the generated package. The cloud has received console text and metadata, not the actual SPB or receipt contents.
- **Passed, user-reported:** installed-header contract compilation and the complete POC package-build/structural-validation script. **Still pending:** Community folder choice/installation, panel registration/rendering, CommBus replies, full toolbar build, Windows companion/live telemetry, Quest 3 and physical iPad checks. No full release has been published.

## Proof-of-concept panel opened in MSFS

- The next user screenshot shows the native `AEROSCIENCE` panel over an actual MSFS flight, displaying `AeroScience registration & CommBus probe`, the read-only explanation and `Waiting for SDK communication service…`.
- **Confirmed by screenshot:** simulator registration and visible desktop rendering of the diagnostic panel. The installed folder path, simulator distribution, aircraft identity, icon appearance, resizing/dragging and VR interaction are not established by this image.
- The waiting label is the probe's initial text. It does not distinguish an uninitialized JavaScript service from an absent companion/reply. CommBus request/reply, runtime DLL loading, Windows companion execution and live aircraft telemetry remain pending.
- Next: locate/check the SDK's native x64 `SimConnect.dll`, copy it beside the chosen companion executable for personal testing, then run simulated telemetry with the toolbar bridge explicitly enabled. Verify changing JSON replies before advancing to the full panel or live telemetry.
- The user's SDK file listing identifies `C:\MSFS 2024 SDK\SimConnect SDK\lib\SimConnect.dll` (84,480 bytes). This establishes its presence only; architecture, actual Windows loading, exports and simulator connectivity still need checking.

## First visible CommBus reply

- Following the native-DLL/bridge setup instructions, the next MSFS screenshot replaces the initial waiting label with JSON beginning `{"protocol":1,"requestId":"…","ok":…}` in the diagnostic panel. The remaining reply is clipped horizontally in the raw diagnostic view.
- **Confirmed by screenshot:** a companion protocol reply reached the in-simulator panel, establishing a POC CommBus request/reply round trip in this session. This also demonstrates execution of the companion's reply path and its native CommBus transport; it does not establish the standalone runtime checker's complete export report, clean-machine installation or live aircraft telemetry.
- The user subsequently confirmed that the JSON request ID keeps changing about once per second while the panel stays open. Recurring POC polling/replies are therefore user-reported passed; the length of this observation was not measured.
- Full reply contents, long-duration stability, simulator disconnect/reconnect behavior, mission mutations and cross-display synchronization still require their own observations. Real aircraft telemetry, full panel, VR and iPad checks remain pending.
- Next: build/install the full panel and test Universal/Realistic missions with simulated telemetry and browser synchronization before selecting live SimConnect telemetry. See the [v0.1.0 testing handoff](v0.1.0-testing-handoff.md).
