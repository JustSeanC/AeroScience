# AeroScience 0.1.1-alpha

Live MSFS 2024 aircraft telemetry, a local SQLite mission archive and a simulator-native toolbar, operated through one Windows application: **AeroScience.exe**.

**Status: implementation and cross-published Windows developer preview; new Windows/MSFS acceptance pending.** v0.1.0 gaming-PC evidence confirmed initial T-38N live telemetry and native toolbar dragging/resizing. The new Mach field, Windows graphical host, real-drive storage migration, sustained live cadence, iPad and VR performance require their own tests. Public alpha distribution is authorized. The complete release remains unpublished until minimum Windows/MSFS acceptance and specific runtime redistribution permission are established.

## User experience

From a complete validated alpha: extract/install graphically, launch AeroScience.exe and fly. The app starts the engine, local dashboard and toolbar bridge, opens the dashboard and retries MSFS automatically. Mission History and complete JSON exports work without MSFS or internet while the selected archive is available. No terminal commands, batch launchers, developer tools, cloud account or separate server setup are part of normal use.

Settings includes independent Mission Database and Scientific Data Library folders, native browsing, usage/free space, restore-default choices and verified migrations that retain originals. Defaults preserve `%LOCALAPPDATA%\AeroScience`. The library has organizational folders and stable identity metadata; no scientific observations or placeholder science products are generated.

Live data includes simulator-provided nullable Mach, displayed as `M 0.82`, persisted with samples and included in exports. Production synthetic telemetry/launchers are removed; old simulated recordings remain readable and labeled. Universal remains the default mission mode; Realistic records identically with advanced science deferred. Compatible aircraft use standard telemetry without a whitelist. Compact VR is removed; there is one responsive Mission/Payload interface.

The recorder targets configurable intervals (default 1 Hz), preserves real observation timestamps/gaps, and distinguishes configured rate, sample cadence and mission-wide average. See the investigation and precise data-safety behavior in [the alpha guide](docs/alpha-testing.md).

## Installation and release status

The first release is built on the Windows gaming PC and uploaded through the **[GitHub Releases website](https://github.com/JustSeanC/AeroScience/releases)**. No self-hosted Actions runner is required. Follow the exact [Windows build, test, finalization and manual upload guide](docs/public-release-windows.md). Testers download **AeroScience-v0.1.1-alpha-win-x64.zip**, extract it and launch AeroScience.exe. **Code → Download ZIP** is source and is not the application.

The local process builds a complete candidate with genuine official-SDK toolbar/runtime evidence, then finalizes the exact tested payload without rebuilding. Minimum alpha acceptance covers real Windows startup, automatic dashboard, offline archive/export, graphical toolbar installation and a short live recording. Extended Mach/cadence/aircraft/storage/iPad/VR checks may remain explicitly pending for the alpha; stable gates retain extended acceptance. The optional [Actions workflow](.github/workflows/alpha-release.yml) supports later candidate preparation/publication with the normal same-repository GitHub token.

Specific native-runtime redistribution permission remains unconfirmed after review of the [official SDK EULA](https://docs.flightsimulator.com/msfs2024/html/1_Introduction/SDK_EULA.htm); see [runtime status](docs/runtime-distribution.md). The Windows app/SDK candidate and minimum hardware checks must be completed on the gaming PC. No full ZIP, final checksum or working public download is claimed before those pass. [INSTALL.md](INSTALL.md) describes the graphical tester experience; [validation results](docs/validation-results.md) distinguish earlier evidence from this build.

## Development

Existing modules are retained: shared models, provider interface, native SimConnect/CommBus adapter, mission coordinator/SQLite store, ASP.NET local host, browser dashboard and SDK toolbar. A small WinForms host adds status, first-run setup, settings, storage management and feedback. Synthetic providers live only in the separate automated test projects and are never shipped.

In the isolated cloud checkout, reuse `/workspace/AeroSciences` (no new worktree unless requested):

```bash
bash scripts/setup-cloud.sh
source scripts/env.sh
dotnet test tests/AeroScience.Tests -c Release --no-restore
npm run test:web
python3 -m unittest discover -s tests -p 'test_*.py' -v
python3 scripts/build/smoke_recovery.py
```

Maintainer Windows builds use `scripts/build/Build-Companion.ps1` and the official SDK `Build-Toolbar.ps1`; see [release documentation](docs/release.md). These commands are not tester setup. Never manufacture an SPB, alter SDK manifests to bypass checks, weaken acceptance, or bundle unapproved SDK/runtime files.

[Architecture](docs/architecture.md) · [SimConnect](docs/simconnect.md) · [Alpha testing](docs/alpha-testing.md) · [Changelog](CHANGELOG.md)
