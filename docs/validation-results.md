# Validation results — 2026-10-16

Current-instance Linux validation. These results do not establish simulator, Windows execution, physical-device or published-environment acceptance.

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

**Unrun/pending:** Windows launch/clean-machine installation; official SDK ABI compilation and toolbar package build; actual Community/Community2024 installation on Store/Steam; real SimConnect flights/lifecycle/units; real CommBus/Coherent panel behavior; Quest 3 PCVR; physical iPad Safari/LAN; full release acceptance. PowerShell/SDK scripts and CI configuration were authored but not executed on their target Windows/GitHub runners here. See `verification.md` for the exact checklist.

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
