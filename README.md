# AeroScience 0.2.1 — Mission Planning & Science Experience

Current development version: **v0.2.1 — Mission Planning & Science Experience**. The primary Windows download remains the conventional setup EXE. Source changes are not a claim of a finished or hardware-tested installer. See the [v0.2.1 completion report](docs/v0.2.1-completion.md) and [exact gaming-PC build/testing/publication guide](docs/v0.2.1-windows-testing.md).

Science now supports independent collection pause/resume, reusable presets and display units, readable cards/charts, offline geographic planning with eight editable patterns, and postflight plan/track comparisons. Toolbar upgrades compare actual SDK manifests and use a common verified progress flow. Original observations stay canonical and local.

Aircraft-agnostic MSFS 2024 research operations, powered by one Windows companion application. Record immediately or prepare a mission plan, acquire simulator-reported atmospheric observations, follow an offline mission map and review/export your research archive without MSFS or internet.

**Source implementation and cloud checks are available. The new Windows/SDK installer and live v0.2.1 acceptance are pending.** Earlier v0.1.1 installation/live-recording results do not verify the changed release. No v0.2.1 setup EXE or public binary download is claimed here.

## Install and operate

After the new Windows candidate passes acceptance, the recommended download will be **AeroScience-Setup-v0.2.1.exe** from [GitHub Releases](https://github.com/JustSeanC/AeroScience/releases). Run Setup, follow graphical storage/toolbar onboarding, then open **Start → AeroScience**. The app starts its services and opens the dashboard automatically. Testers need no SDK, terminal, PowerShell, manual DLL copy or separate server. The installer retains the self-contained Windows application, authorized native SimConnect runtime, genuine SDK-built toolbar, prerequisites, shortcuts and Windows uninstaller. A portable ZIP is secondary.

- **Mission Control:** start spontaneously, see real aircraft telemetry/Mach and read-only standard flight guidance, follow the actual track and add timestamped observation markers/notes.
- **Mission Planner:** create/edit saved plans using General Research, Atmospheric Profile, Geographic Survey or Observation Flight. Set optional objectives, intended aircraft, departure/destination, ordered waypoints, survey polygons, altitude range, duration, instruments and notes. Each recording keeps its own immutable plan snapshot.
- **Science:** enable ambient temperature, pressure, wind and density channels independently, choose 1–60-second intervals, inspect real values/units, quality, last successful acquisition, saved/rejected counts, missing intervals, cadence and graphs. No modeled science is substituted for missing data.
- **Mission History:** review flight and science summaries, maps, markers, notes and paged observations; export complete versioned JSON, flight/science/events CSV, GPX, KML and a reproducible text report.
- **Settings:** independently choose Mission Database and Scientific Data Library folders. Existing verified migrations retain originals. Trusted local Windows **Storage → Data Management** provides separate flight/science clearing, previews, typed confirmations and optional verified backups. No browser deletion API exists.

Universal is the default. Realistic supports the same compatible aircraft and scientific acquisition; advanced operating limits/calibration remain deferred. There is no aircraft whitelist or separate Compact VR interface. Neither mission nor science acquisition controls the aircraft/autopilot/flight plan. Science values are **simulator-reported observations at the user aircraft**, not independent calibrated real-world weather measurements.

## Build, validation and release

The [completion report](docs/v0.2.1-completion.md) records implemented scope, checks, limitations and Windows/device tests still needed. [Science operations](docs/science-operations.md) explains storage, provenance, estimates and exports. [SDK research](docs/sdk-research-v0.2.0.md) lists exact variables, units and guidance limitations.

On the gaming PC, use the [Windows installer build/test/manual publication guide](docs/public-release-windows.md). No self-hosted GitHub Actions runner is required. Genuine SDK compilation, runtime loading/export checks, the existing private-safe redistribution attestation and actual release acceptance remain mandatory. Finalization preserves the exact tested setup bytes and generates its SHA-256 checksum. The optional Actions workflow remains available for future automation.

For isolated cloud development, reuse the existing checkout:

```bash
bash scripts/setup-cloud.sh
source scripts/env.sh
dotnet test tests/AeroScience.Tests -c Release --no-restore
npm run test:web
python3 -m unittest discover -s tests -p 'test_*.py' -v
python3 scripts/build/smoke_recovery.py
```

Synthetic sources exist only in automated test projects. Native Windows GUI execution, the proprietary official SDK, MSFS, Quest 3 and physical iPad testing require their respective environments; cross-compilation and browser fixtures do not establish those passes.

[Installation](INSTALL.md) · [Architecture](docs/architecture.md) · [Validation](docs/validation-results.md) · [Changelog](CHANGELOG.md)
