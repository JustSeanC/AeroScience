# AeroScience 0.4.1 — Scientific Review & Mission Readiness (development)

v0.4.1 adds full-page Science Map, Compare, Research Analysis and Results routes; explicit saved-plan selection before recording; instrument-first preparation; durable evidence-based post-flight reconstruction; repeated same-line transects; an independent provider/collection library; and rendered offline Help & Support. See [completion and remaining acceptance](docs/v0.4.1-completion.md), [pilot guide](docs/v0.4.1-pilot-guide.md) and [Windows testing](docs/v0.4.1-windows-testing.md).

**This is development source, not a released or hardware-accepted v0.4.1 installer.** Current-source provider, guided ENVI and full genuine workspace acceptance remain pending while the development environment's HTTP tunnel is unavailable. Retained genuine terrain/Landsat/Sentinel review checks and deterministic fixtures are reported separately. The release gate still refuses pending scientific evidence. Earlier v0.4.0 evidence and published installers remain unchanged. v0.5.0 retains its Advanced Instrument Calibration & Aircraft Operating Constraints scope.

New installations suggest OpenStreetMap / Streets after an explicit privacy choice with offline-only available before the first tile request; upgrades preserve map preferences. Storage defaults are **25 GB flight / 50 GB science**, configurable or Unlimited; only registered regenerable caches can be automatically removed, with pinned research protected. Optional GitHub update notifications never run installers automatically. Mission planning, atmospheric instruments, offline archives, dual stores, native toolbar controls and Universal/Realistic modes remain.

**Cloud tests and source changes do not verify Windows/MSFS hardware. No v0.4.1 installer or binary publication is claimed from Linux.** Build the real setup on the gaming PC using the official SDK and existing runtime authorization. The primary download is `AeroScience-Setup-v0.4.1.exe`; portable ZIPs are secondary.

Automatic scientific data is now the normal supported terrain/thermal/multispectral workflow: [pilot quick start](docs/remote-sensing/quick-start.md). Consent is separate from basemaps. Hyperspectral uses the graphical ENVI import wizard and compatible local data; optional NASA discovery is informational and automatic hyperspectral downloading is not required. [Provider limits and genuine acceptance](docs/remote-sensing/automatic-data.md).

## Install and operate

After the Windows build succeeds, the recommended download will be **AeroScience-Setup-v0.4.1.exe** from [GitHub Releases](https://github.com/JustSeanC/AeroScience/releases). Run Setup, follow graphical storage/toolbar onboarding, then open **Start → AeroScience**. The app starts its services and opens the dashboard automatically. Testers need no SDK, terminal, PowerShell, manual DLL copy or separate server. The installer retains the self-contained Windows application, authorized native SimConnect runtime, genuine SDK-built toolbar, prerequisites, shortcuts and Windows uninstaller. A portable ZIP is secondary.

- **Mission Control:** choose a saved plan or Unplanned Flight, preview its route, explicitly start recording, see real aircraft telemetry/Mach and read-only standard flight guidance, follow the actual track and add timestamped observation markers/notes.
- **Mission Planner:** create/edit saved plans using General Research, Vertical Profile, Geographic Survey, Observation Flight, Cloud Structure Survey or Atmospheric Transect. Use versioned payload profiles, AGL/groundspeed/overlap recommendations and native geodesic pattern previews. Set optional objectives, intended aircraft, departure/destination, ordered waypoints, survey polygons, altitude range, duration, instruments and notes. Each recording keeps its own immutable plan snapshot.
- **Remote sensing:** consent to automatic bounded terrain/Landsat/Sentinel preparation, or optionally import advanced real collections; sample thermal/reflectance/spectra/elevation at approximate flight footprints, and inspect/export saved source-aware products. See [supported datasets and limitations](docs/remote-sensing/sources.md).
- **Science:** enable ambient temperature, pressure, wind and density channels independently, choose 1–60-second intervals, inspect real values/units, quality, last successful acquisition, saved/rejected counts, missing intervals, cadence and graphs. No modeled science is substituted for missing data.
- **Science Workspace:** review one, several or all missions on an indexed native-pixel science map; inspect source dates, compare defensible quantities, open structured instrument results and exports.
- **Mission History:** search the offline logbook and open Science Review or Replay directly. Advanced legacy detail remains available. Inspect actual timestamps/configuration/gaps; compare modeled footprint union with saved valid native-pixel support; generate native-grid mission sample composites or separately labeled retained-source mosaics; export GeoTIFF and hashed research ZIPs alongside existing JSON/CSV/GPX/KML.
- **Settings:** independently choose Mission Database and Scientific Data Library folders. Existing verified migrations retain originals. Trusted local Windows **Storage & Data → Data Management** provides separate flight/science clearing, previews, typed confirmations and optional verified backups. No browser deletion API exists.

Universal is the default. Realistic supports the same compatible aircraft and scientific acquisition; profile geometry and qualitative weather cautions are available; advanced calibration/failure simulation remains deferred. Remote instruments use actual automatically prepared or advanced imported observations, not a new simulator camera or point-cloud generator. There is no aircraft whitelist or separate Compact VR interface. Neither mission nor science acquisition controls the aircraft/autopilot/flight plan. Atmospheric values are **simulator-reported observations at the user aircraft**; remote-sensing values come from historical external sources with their original dates and limitations.

## Community & Support

Need help with AeroScience? Join our Discord community for support, troubleshooting, questions, feature requests, and discussions with other pilots.

[Join our Discord](https://discord.gg/mpYyazCqvJ). Membership is optional. You can also use **Collect Feedback / Logs**, the [pilot guide](docs/v0.4.1-pilot-guide.md) and [GitHub issues](https://github.com/JustSeanC/AeroScience/issues). The dashboard and paired-device browser expose these options under **Help & Support**; the Discord link opens externally.

## Build, validation and release

The [completion report](docs/v0.4.1-completion.md) records implemented scope, checks, limitations and Windows/device tests still needed. The [CI repair report](docs/v0.4.1-ci-repair.md) separates local results from pending GitHub Actions acceptance. [Science operations](docs/science-operations.md) explains storage, provenance, estimates and exports. [SDK research](docs/sdk-research-v0.2.0.md) lists exact variables, units and guidance limitations.

On the gaming PC, use the [v0.4.1 Windows checklist](docs/v0.4.1-windows-testing.md) and [installer build/test/manual publication guide](docs/public-release-windows.md). No self-hosted GitHub Actions runner is required. Genuine SDK compilation, runtime loading/export checks, private-safe redistribution attestation and automated technical tests remain mandatory. Finalization preserves the exact tested setup bytes and generates a SHA-256 checksum. Current genuine scientific evidence is pending and blocks distribution. After those gates pass, v0.4.1 stops at an installer candidate until actual installed tests A–H and device/upgrade checks are recorded. A graphical maintainer checklist avoids handwritten JSON. The optional Actions preparation workflow remains available; its unaccepted science-release publication path is blocked.

For isolated cloud development, reuse the existing checkout:

```bash
bash scripts/setup-cloud.sh
source scripts/env.sh
dotnet test tests/AeroScience.Tests -c Release --no-restore
npm run test:web
python3 -m unittest discover -s tests -p 'test_*.py' -v
dotnet build tests/AeroScience.TestHost -c Release --no-restore --nologo --output artifacts/recovery-test-host
python3 scripts/build/smoke_recovery.py --host-dll artifacts/recovery-test-host/AeroScience.TestHost.dll
```

Synthetic sources exist only in automated test projects. Native Windows GUI execution, the proprietary official SDK, MSFS, Quest 3 and physical iPad testing require their respective environments; cross-compilation and browser fixtures do not establish those passes.

[Installation](INSTALL.md) · [Architecture](docs/architecture.md) · [Validation](docs/validation-results.md) · [Changelog](CHANGELOG.md)

Mission Planner walkthrough and revised draft/map workflows: [first mission guide](docs/mission-planner.md).
