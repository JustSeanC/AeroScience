# Scientific flying quick start — v0.3.1

Install AeroScience, launch it from Start and open its dashboard. Normal users do not need a terminal, a separate server, GIS tools or a JSON manifest.

## Set up once

1. Complete storage and Community toolbar setup. Your mission archive and Science Library can use different drives. Existing records/settings survive upgrades.
2. Open **Settings → Scientific Data Sources**. Review the separate scientific-data privacy notice before enabling automatic retrieval: providers receive queried geography and your IP address; downloads go directly to your PC. Basemap permission is independent. Local datasets remain usable offline.
3. Select each instrument's source mode. **Terrain, thermal and multispectral** can use automatic preparation, local datasets only, or local datasets with automatic fallback. **Hyperspectral** uses your compatible ENVI dataset: choose **Import Hyperspectral Dataset** and follow the [four-step beginner guide](using-your-own-hyperspectral-dataset.md).
4. Under **Online access, predictive preparation & download budgets**, keep automatic lookahead unless you need advanced control. Defaults: **64 MiB per preparation**, **256 MiB per rolling application hour**, **one download at a time**, and bounded background retrieval. Unmetered-only pauses downloads when network cost cannot be established. You can pause/resume/cancel preparation.
5. **Settings → Storage & Data** retains default **25 GB mission / 50 GB science** limits, selectable Unlimited, cache-only automatic cleanup and pinned protection. Downloaded sources are retained scientific originals. Originals, observations, products and backups are never silently deleted to make room.

## Planned mission

Create your route/survey in Mission Planner, enable instruments and save. **Prepare scientific data** starts progressive bounded batches; it no longer stops at eight cells. Watch ready/preparing/awaiting/unavailable counts, transfer and pending ceiling. Additional distant workload is enumerated progressively rather than triggering a continental download. Pause/Resume/Cancel controls are on the plan's readiness row.

The saved route helps when actual movement follows it. It does not require LNAV or any particular aircraft. A departure from the route switches priority to observed ground movement. The existing approximate sensor footprint expands the preparation area; this does not prove complete scientific coverage or calibrated swath accuracy.

## Unplanned flight

Enable an instrument and fly with fresh MSFS telemetry. The predictor waits for consistent fresh positions, uses observed geographic movement/ground track, and prepares a forward corridor. It filters turn rate, curves sustained turns and gives repeated survey passes the benefit of retained data. Heading does not replace observed wind-drift motion. Pauses, gaps, reconnects and teleportation reset/suspend prediction.

Mission Control's **Scientific Data Readiness** shows lookahead, conservative ready-ahead status, queue/download activity and resource limits. Its map layer distinguishes ready, preparing, pending, partially usable and unavailable areas without moving your viewport. If preparation falls behind, a warning appears. No network provider can guarantee continuous coverage.

Start a mission to record science. While data are missing, **flight telemetry keeps recording**, an unavailable/gap reason is retained, other instruments continue independently, and valid science resumes on subsequent packets. Later downloads never backfill missing flight-time acquisitions.

## Inspect the source and results

**Settings → Scientific Data Sources** lists actual datasets, original provider/date, numeric bands, native resolution, coverage and validation status. **View Coverage** shows where a source can be used; masks/nodata may still prevent individual observations. A small local dataset can be partly usable within a larger requested preparation cell.

**Science Collection → Remote-Sensing Products → View Data** shows the retained product, footprint, numeric views/spectrum, quality, source date, aircraft packet time and processing time. Export JSON/CSV with provenance. Close MSFS and disconnect internet: previously saved missions/products and eligible local source sampling remain available while their chosen storage is connected.

- Thermal uses historical Landsat 8/9 L2SP surface temperature and quality masks.
- Multispectral uses historical Sentinel-2 L2A reflectance/SCL; RGB is derived from numeric bands.
- Terrain uses numeric 3DEP DEM or Copernicus GLO-30 DSM, retaining their different limitations/datums. These are not aircraft LiDAR returns.
- Hyperspectral uses your genuine ENVI source within its real coverage. NASA metadata discovery is optional information, not a required import or automatic-download flow.
- Atmospheric channels remain simulator-reported telemetry.

[Scientific Data Sources](scientific-data-sources.md) · [Hyperspectral beginner guide](using-your-own-hyperspectral-dataset.md) · [Provider details](automatic-data.md) · [Storage](storage.md) · [Exports](exports.md) · [Advanced import](sources.md)
