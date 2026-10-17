# AeroScience 0.1.1-alpha

Live MSFS 2024 aircraft telemetry, a local SQLite mission archive and a simulator-native toolbar, operated through one Windows application: **AeroScience.exe**.

**Status: implementation and cross-published Windows developer preview; new Windows/MSFS acceptance pending.** v0.1.0 gaming-PC evidence confirmed initial T-38N live telemetry and native toolbar dragging/resizing. The new Mach field, Windows graphical host, real-drive storage migration, sustained live cadence, iPad and VR performance require their own tests. No installation-ready friends-only release or public binary upload is claimed.

## User experience

From a complete validated alpha: extract/install graphically, launch AeroScience.exe and fly. The app starts the engine, local dashboard and toolbar bridge, opens the dashboard and retries MSFS automatically. Mission History and complete JSON exports work without MSFS or internet while the selected archive is available. No terminal commands, batch launchers, developer tools, cloud account or separate server setup are part of normal use.

Settings includes independent Mission Database and Scientific Data Library folders, native browsing, usage/free space, restore-default choices and verified migrations that retain originals. Defaults preserve `%LOCALAPPDATA%\AeroScience`. The library has organizational folders and stable identity metadata; no scientific observations or placeholder science products are generated.

Live data includes simulator-provided nullable Mach, displayed as `M 0.82`, persisted with samples and included in exports. Production synthetic telemetry/launchers are removed; old simulated recordings remain readable and labeled. Universal remains the default mission mode; Realistic records identically with advanced science deferred. Compatible aircraft use standard telemetry without a whitelist. Compact VR is removed; there is one responsive Mission/Payload interface.

The recorder targets configurable intervals (default 1 Hz), preserves real observation timestamps/gaps, and distinguishes configured rate, sample cadence and mission-wide average. See the investigation and precise data-safety behavior in [the alpha guide](docs/alpha-testing.md).

## Installation and release status

Read [INSTALL.md](INSTALL.md) for the graphical flow and [the testing guide](docs/alpha-testing.md) for acceptance. **Code → Download ZIP is source**, not a ready-to-run app. Old Actions artifacts are v0.1.0 previews. Public CI no longer uploads alpha binaries automatically; the explicit [private release workflow](.github/workflows/alpha-release.yml) verifies a private destination and requires genuine SDK output, hardware evidence and permitted native-runtime distribution.

Release blockers include a new official Windows SDK build/receipt, Windows UI/drive and live Mach/cadence testing, native runtime redistribution evidence and an approved private distribution environment. The Linux build cannot certify them. The [chronological evidence](docs/validation-results.md) separates prior gaming-PC results from new automated checks.

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
