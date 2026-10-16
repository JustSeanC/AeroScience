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
