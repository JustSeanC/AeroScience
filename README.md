# AeroScience 0.1.0

An aircraft-independent airborne research foundation for Microsoft Flight Simulator 2024: a C# science engine, local web dashboard, SQLite mission archive, and a simulator-native toolbar project.

**Status: development foundation / companion preview. Not a verified Community release.** The simulated workflow runs and is tested on Linux. Windows x64 publishing succeeds. Real Windows execution, official SDK compilation, live MSFS, toolbar loading, Quest 3, and iPad Safari still require the checks in [verification](docs/verification.md). No installation-ready `AeroScience-v0.1.0.zip` is claimed or supplied.

For gaming-PC installation, start with the [Windows installation and live-testing workbook](docs/windows-testing.md): sign in to GitHub and download the **Windows-Companion-unverified-in-MSFS** artifact from a successful [Actions build](https://github.com/JustSeanC/AeroScience/actions/workflows/ci.yml), extract it, run simulated mode, then check the SDK/runtime and build the toolbar proof of concept. GitHub's source ZIP does not include the executable. The three foundation commits are already backed up on `main`.

## What works in the tested development workflow

- Explicitly labeled synthetic telemetry, including position, altitude, speeds, heading/track, attitude, and vertical speed.
- Named missions, configurable recording frequency, start/end controls, live state synchronization through server-sent events, and history/detail views.
- SQLite transactions, crash recovery, preserved partial recordings, and a single engine owner for each database. Displays can close without stopping recording.
- **Universal — Fly Anything** (default) and **Realistic — Authentic Research** mission modes, persisted and shared with every interface. Older records migrate to Universal. Both record the same telemetry in 0.1.0. Realistic instrument limits, quality scoring, calibration, and sensors are future features.
- Localhost by default; optional private-LAN binding, local pairing-code retrieval, expiring sessions, CSRF protection, same-origin commands, and Host validation.
- A native Windows x64 SimConnect provider and MSFS 2024 SU5+ CommBus toolbar bridge, compiled but **not exercised against the simulator**.
- SDK toolbar source with a read-only proof of concept, full mission controls, science-mode selector, and compact VR layout. SDK compilation and Coherent/VR rendering remain pending.

There is no aircraft whitelist. Default, freeware, payware, fixed-wing and rotary-wing aircraft are intended to use the same standard telemetry path. The NASA aircraft mentioned in planning are future research presets, not compatibility restrictions. No aircraft files are modified. Unknown aircraft titles fall back to “Unknown MSFS aircraft”; unavailable AGL is null. Actual compatibility with individual add-ons remains subject to their standard SimConnect data and simulator testing.

## Run from source

Install the SDK pinned in `global.json` (.NET 10 LTS). No Node runtime or cloud account is needed by the application.

```powershell
dotnet restore AeroScience.sln --locked-mode
dotnet run --project src/AeroScience.Engine -- --AeroScience:Telemetry=Simulated
```

On the same PC, open `http://127.0.0.1:5080` in your browser. That address is a local application address, not a hosted cloud preview. Leave the engine console running while recording. Use the mode selector, enter a mission name, start recording, then end the mission and review Mission History.

For this cloud workspace:

```bash
bash scripts/setup-cloud.sh
source scripts/env.sh
export AeroScience__DataDirectory="$PWD/artifacts/dev-data"
dotnet run --project src/AeroScience.Engine --no-launch-profile
```

Cloud tasks already have isolated checkouts. Reuse `/workspace/AeroSciences`; do not create a worktree unless explicitly requested.

## Verify and build

```bash
dotnet test AeroScience.sln -c Release --no-restore
npm ci --ignore-scripts
npm run check:web
# Use CHROMIUM_PATH=/usr/bin/chromium when using an existing Linux Chromium.
npm run test:web
python3 -m unittest discover -s tests -p 'test_*.py' -v
python3 scripts/build/smoke_recovery.py
```

Browser tests need Node 22+ and Chromium (`npx playwright install chromium` on a development PC without it). These are development dependencies only. The browser interface is plain HTML/CSS/JavaScript, served entirely from the companion; the .NET build copies it to build and publish outputs.

Windows publishing: `pwsh scripts/build/Build-Companion.ps1`. SDK toolbar building: `pwsh scripts/build/Build-Toolbar.ps1 -SdkRoot 'YOUR SDK DIRECTORY' -ProofOfConcept`, then repeat without `-ProofOfConcept` after the in-simulator probe passes. A configured x64 Native Tools shell and the official SDK are required for the latter.

See [installation](INSTALL.md), [architecture/API](docs/architecture.md), [SimConnect](docs/simconnect.md), [toolbar research and packaging](docs/toolbar.md), and [release process](docs/release.md).

Scientific sensors, mapping, full replay, aircraft presets, cloud services, multiplayer, an auto-updater, tray UI and a one-click installer are outside this foundation. A web manifest prepares future PWA work; offline service-worker caching is deliberately not implemented.

MSFS and Microsoft marks belong to their owners. AeroScience is not affiliated with Microsoft, Asobo or NASA.
