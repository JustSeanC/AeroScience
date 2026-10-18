# AeroScience 0.3.0 — Remote Sensing & Research Payloads Alpha

Current source adds automatic real-data terrain, thermal and multispectral preparation, optional advanced hyperspectral ENVI import, geographic sampling/viewers/exports and local evidence. Scientific source dates stay distinct from simulator flight and processing dates. There are no fabricated worldwide sensors or bundled scientific archives. See the [remote-sensing quick start](docs/remote-sensing/quick-start.md), [completion report](docs/v0.3.0-completion.md) and [Windows one-command build/test/publication guide](docs/v0.3.0-windows-testing.md).

New installations suggest OpenStreetMap / Streets after an explicit privacy choice with offline-only available before the first tile request; upgrades preserve map preferences. Storage defaults are **25 GB flight / 50 GB science**, configurable or Unlimited; only registered regenerable caches can be automatically removed, with pinned research protected. Optional GitHub update notifications never run installers automatically. Mission planning, atmospheric instruments, offline archives, dual stores, native toolbar controls and Universal/Realistic modes remain.

**Cloud tests and source changes do not verify Windows/MSFS hardware. No v0.3.0 installer or binary publication is claimed from Linux.** Build the real setup on the gaming PC using the official SDK and existing runtime authorization. The primary download is `AeroScience-Setup-v0.3.0.exe`; portable ZIPs are secondary.

Automatic scientific data is now the normal supported terrain/thermal/multispectral workflow: [pilot quick start](docs/remote-sensing/quick-start.md). Consent is separate from basemaps. Hyperspectral supports genuine NASA catalog discovery and advanced ENVI import; automatic spectral preparation is not yet implemented. [Provider limits and genuine acceptance](docs/remote-sensing/automatic-data.md).

## Install and operate

After the Windows build succeeds, the recommended download will be **AeroScience-Setup-v0.3.0.exe** from [GitHub Releases](https://github.com/JustSeanC/AeroScience/releases). Run Setup, follow graphical storage/toolbar onboarding, then open **Start → AeroScience**. The app starts its services and opens the dashboard automatically. Testers need no SDK, terminal, PowerShell, manual DLL copy or separate server. The installer retains the self-contained Windows application, authorized native SimConnect runtime, genuine SDK-built toolbar, prerequisites, shortcuts and Windows uninstaller. A portable ZIP is secondary.

- **Mission Control:** start spontaneously, see real aircraft telemetry/Mach and read-only standard flight guidance, follow the actual track and add timestamped observation markers/notes.
- **Mission Planner:** create/edit saved plans using General Research, Atmospheric Profile, Geographic Survey or Observation Flight. Set optional objectives, intended aircraft, departure/destination, ordered waypoints, survey polygons, altitude range, duration, instruments and notes. Each recording keeps its own immutable plan snapshot.
- **Remote sensing:** consent to automatic bounded terrain/Landsat/Sentinel preparation, or optionally import advanced real collections; sample thermal/reflectance/spectra/elevation at approximate flight footprints, and inspect/export saved source-aware products. See [supported datasets and limitations](docs/remote-sensing/sources.md).
- **Science:** enable ambient temperature, pressure, wind and density channels independently, choose 1–60-second intervals, inspect real values/units, quality, last successful acquisition, saved/rejected counts, missing intervals, cadence and graphs. No modeled science is substituted for missing data.
- **Mission History:** review flight and science summaries, maps, markers, notes and paged observations; export complete versioned JSON, flight/science/events CSV, GPX, KML and a reproducible text report.
- **Settings:** independently choose Mission Database and Scientific Data Library folders. Existing verified migrations retain originals. Trusted local Windows **Storage & Data → Data Management** provides separate flight/science clearing, previews, typed confirmations and optional verified backups. No browser deletion API exists.

Universal is the default. Realistic supports the same compatible aircraft and scientific acquisition; advanced operating limits/calibration remain deferred. Remote instruments use actual automatically prepared or advanced imported observations, not a new simulator camera or point-cloud generator. There is no aircraft whitelist or separate Compact VR interface. Neither mission nor science acquisition controls the aircraft/autopilot/flight plan. Science values are **simulator-reported observations at the user aircraft**, not independent calibrated real-world weather measurements.

## Build, validation and release

The [completion report](docs/v0.3.0-completion.md) records implemented scope, checks, limitations and Windows/device tests still needed. [Science operations](docs/science-operations.md) explains storage, provenance, estimates and exports. [SDK research](docs/sdk-research-v0.2.0.md) lists exact variables, units and guidance limitations.

On the gaming PC, use the [Windows installer build/test/manual publication guide](docs/public-release-windows.md). No self-hosted GitHub Actions runner is required. Genuine SDK compilation, runtime loading/export checks, private-safe redistribution attestation and automated technical tests remain mandatory. Finalization preserves compiled setup bytes and generates a SHA-256 checksum automatically; **no mandatory ACCEPTANCE.json editing**. Hardware QA stays separately labeled not verified. The optional Actions workflow remains available for future automation.

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

Mission Planner walkthrough and revised draft/map workflows: [first mission guide](docs/mission-planner.md).
