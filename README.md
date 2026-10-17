# AeroScience 0.1.1-alpha

Live MSFS 2024 aircraft telemetry, a local SQLite mission archive and a simulator-native toolbar, operated through one Windows application: **AeroScience.exe**.

**Status: Windows installer and guided onboarding implemented; new installer/Windows/MSFS acceptance pending.** The previous ZIP candidate built successfully on the gaming PC and its app window opened. That evidence does not validate this changed installer. Public alpha distribution is authorized; a complete setup release remains unpublished until its installation, first-run, upgrade, uninstall and minimum live-flight checks pass.

## User experience

From a complete validated alpha: run **AeroScience-Setup-v0.1.1-alpha.exe**, follow guided storage/toolbar setup and launch from **Start → AeroScience**. The installer provides shortcuts, bundled prerequisites and Windows Installed Apps/uninstall. The app starts the engine, local dashboard and toolbar bridge, opens the dashboard and retries MSFS automatically. Mission History and complete JSON exports work without MSFS or internet while the selected archive is available. No extraction, terminal commands, developer tools, cloud account or separate server setup are part of normal use.

Settings includes independent Mission Database and Scientific Data Library folders, native browsing, usage/free space, restore-default choices and verified migrations that retain originals. Defaults preserve `%LOCALAPPDATA%\AeroScience`. The library has organizational folders and stable identity metadata; no scientific observations or placeholder science products are generated.

Live data includes simulator-provided nullable Mach, displayed as `M 0.82`, persisted with samples and included in exports. Production synthetic telemetry/launchers are removed; old simulated recordings remain readable and labeled. Universal remains the default mission mode; Realistic records identically with advanced science deferred. Compatible aircraft use standard telemetry without a whitelist. Compact VR is removed; there is one responsive Mission/Payload interface.

The recorder targets configurable intervals (default 1 Hz), preserves real observation timestamps/gaps, and distinguishes configured rate, sample cadence and mission-wide average. See the investigation and precise data-safety behavior in [the alpha guide](docs/alpha-testing.md).

## Installation and release status

The first release is built on the Windows gaming PC using Inno Setup and uploaded through the **[GitHub Releases website](https://github.com/JustSeanC/AeroScience/releases)**. No self-hosted Actions runner is required. Follow the exact [Windows installer build, test, finalization and manual upload guide](docs/public-release-windows.md). Testers download **AeroScience-Setup-v0.1.1-alpha.exe**. A portable ZIP may be offered as a secondary download. **Code → Download ZIP** is source and is not the application.

The local process builds a complete installer candidate with genuine official-SDK toolbar/runtime evidence and a signed Microsoft Visual C++ prerequisite, then finalizes the identical tested EXE without rebuilding. Eleven minimum alpha checks include Windows installation, wizard, upgrade/uninstall preservation, automatic dashboard, offline archive/export, toolbar installation and a short live recording. Extended Mach/cadence/aircraft/storage/iPad/VR checks may remain explicitly pending; stable gates retain extended acceptance. The optional [Actions workflow](.github/workflows/alpha-release.yml) supports later candidate preparation/publication with the normal same-repository GitHub token.

The maintainer has confirmed native SimConnect redistribution authorization with no additional conditions reported. Supporting correspondence stays private; a public summary binds authorization to the actual DLL hash. See [runtime attestation and privacy](docs/runtime-distribution.md). The new installer/SDK candidate and acceptance must be completed on Windows. No finished installer, final installer checksum or working public download is claimed before those pass. [INSTALL.md](INSTALL.md) describes the graphical tester experience; [validation results](docs/validation-results.md) distinguish earlier evidence from this build.

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
