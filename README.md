# AeroScience 0.4.2 — Research Workstation & Scientific Visualization (development)

v0.4.2 refines the research workflow with a complete-scope Science Map, independent instrument layers, exact-value comparisons, instrument findings, full-page Download Activity, searchable source collections, offline help and responsive navigation. See [completion and acceptance status](docs/v0.4.2-completion.md), [pilot guide](docs/v0.4.2-pilot-guide.md) and [Windows testing](docs/v0.4.2-windows-testing.md).

**Development source; no v0.4.2 installer or hardware acceptance is claimed here.** Fresh genuine-data checks passed for four public providers, guided ENVI import, native-grid composites and retained-data review. Flight inputs were explicitly synthetic. Human usability scores, screen-reader conformance, Windows/MSFS, physical iPad Safari and Quest 3 remain unverified. Earlier release artifacts and v0.4.1 reports are preserved.

AeroScience is a free, open-source, local-first research companion for Microsoft Flight Simulator 2024. No account or mandatory cloud service is required. Mission history and retained scientific data remain accessible offline. v0.5.0 retains Advanced Instrument Calibration & Aircraft Operating Constraints as its central scope; see the owner’s [additional roadmap backlog](docs/roadmap/v0.5.0-backlog.md).

**Maintainers:** build the real setup on Windows using the official SDK and existing private runtime authorization. The expected primary asset is `AeroScience-Setup-v0.4.2.exe`; portable ZIPs remain secondary. No release is published by this change.

Automatic scientific data is now the normal supported terrain/thermal/multispectral workflow: [pilot quick start](docs/remote-sensing/quick-start.md). Consent is separate from basemaps. Hyperspectral uses the graphical ENVI import wizard and compatible local data; optional NASA discovery is informational and automatic hyperspectral downloading is not required. [Provider limits and genuine acceptance](docs/remote-sensing/automatic-data.md).

## Install and operate

After the Windows build succeeds, the recommended download will be **AeroScience-Setup-v0.4.2.exe** from [GitHub Releases](https://github.com/JustSeanC/AeroScience/releases). Run Setup, follow graphical storage/toolbar onboarding, then open **Start → AeroScience**. The app starts its services and opens the dashboard automatically. Testers need no SDK, terminal, PowerShell, manual DLL copy or separate server. The installer retains the self-contained Windows application, authorized native SimConnect runtime, genuine SDK-built toolbar, prerequisites, shortcuts and Windows uninstaller. A portable ZIP is secondary.

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

[Join our Discord](https://discord.gg/mpYyazCqvJ). Membership is optional. You can also use **Collect Feedback / Logs**, the [pilot guide](docs/v0.4.2-pilot-guide.md) and [GitHub issues](https://github.com/JustSeanC/AeroScience/issues). The dashboard and paired-device browser expose these options under **Help & Support**; the Discord link opens externally.

## Build, validation and release

The [completion report](docs/v0.4.2-completion.md) records implemented scope, checks, limitations and Windows/device tests still needed. The [CI repair report](docs/v0.4.1-ci-repair.md) separates local results from actual hosted [GitHub Actions runs](https://github.com/JustSeanC/AeroScience/actions/workflows/ci.yml). [Science operations](docs/science-operations.md) explains storage, provenance, estimates and exports. [SDK research](docs/sdk-research-v0.2.0.md) lists exact variables, units and guidance limitations.

On the gaming PC, use the [v0.4.2 Windows checklist](docs/v0.4.2-windows-testing.md) and [installer build/test/manual publication guide](docs/public-release-windows.md). No self-hosted GitHub Actions runner is required. Genuine SDK compilation, runtime loading/export checks, private-safe redistribution attestation and automated technical tests remain mandatory. Finalization preserves the exact tested setup bytes and generates a SHA-256 checksum. v0.4.2 genuine reports retain their original source-bound evidence. The v0.5.0 development reports are pending; no new scientific acceptance or release is claimed. v0.4.2 stops at an installer candidate until actual installed tests A–H and device/upgrade checks are recorded. A graphical maintainer checklist avoids handwritten JSON. The optional Actions preparation workflow remains available; its unaccepted science-release publication path is blocked.

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

[Installation](INSTALL.md) · [Architecture](docs/architecture.md) · [Validation](docs/verification.md) · [Changelog](CHANGELOG.md)

Mission Planner walkthrough and revised draft/map workflows: [first mission guide](docs/mission-planner.md).

Development documentation: [central index](docs/README.md) · [v0.5.0 implementation tracker](docs/roadmap/v0.5.0-development-tracker.md). Main remains the v0.4.2 external-QA baseline; development is not a published release.
