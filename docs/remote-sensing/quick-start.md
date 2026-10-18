# Automatic remote-sensing quick start — v0.3.0 alpha

AeroScience can discover and prepare **real historical Earth data automatically** for terrain, thermal and multispectral instruments. Pilots do not need QGIS, Python, a terminal, a hand-written manifest, or a manual satellite download. Satellite observation time is separate from your simulated flight time. These instruments are not live aircraft cameras or measured aircraft LiDAR.

## Set up once

1. Run `AeroScience-Setup-v0.3.0.exe`, choose your storage and Community toolbar options, and launch AeroScience. It starts the local services and browser dashboard automatically.
2. During guided setup, review the **separate scientific-data privacy disclosure** and optionally enable automatic scientific-data retrieval. You can instead leave it offline-only. Revisit this choice in **Settings → Scientific Data Sources → Automatic Scientific Data Access**. Public providers receive geographic queries and your IP address; downloads go directly to your PC, without an AeroScience download server, paid shared key or scientific-data account for the supported terrain/thermal/multispectral paths.
3. Choose basemap privacy independently. Online OpenStreetMap consent does **not** authorize scientific downloads. Offline basemaps can be used with online scientific preparation, and vice versa.
4. Review **Settings → Storage & Data**. Defaults remain **25 GB Flight/Mission / 50 GB Scientific Library**, cache-only automatic cleanup and pinned protection. Mission history and the Science Library can use separate internal/external drives. Prepared numeric source subsets are retained scientific originals, not disposable caches.
5. In scientific-access Settings, choose the maximum download budget (default 64 MiB per preparation), 1–2 concurrent preparations (default 1), background retrieval, optional unmetered-only access, search date window and cloud policy. Unknown Windows network cost pauses unmetered-only downloads. Download ceilings and conservative staging bounds are shown; actual transfers and retained file sizes vary.

## Prepare a planned flight

1. In **Mission Planner**, draw a small survey polygon or route and enable **Terrain Scanner**, **Thermal Imager** and/or **Multispectral Imager**. Choose Universal (default) or Realistic. No aircraft whitelist applies.
2. Save the plan. When background retrieval is enabled, bounded preflight preparation is queued automatically. **Prepare scientific data** explicitly starts or retries preparation, including when background retrieval is disabled.
3. Inspect **Scientific Data Readiness**. It shows provider, actual dataset, requested coverage, original observation date, scene quality, transfer/staging bounds and status. Wait for **Ready** before takeoff where practical. **Limited Coverage** is a limitation, not a successful flight observation. Large routes/surveys prepare at most eight small cells per action and explicitly report partial coverage; this is not complete corridor coverage.
4. If needed, use **Settings → Automatic Scientific Data Access → Prepare a small area** for a specific latitude/longitude. Preparation creates and validates its internal manifest automatically.
5. Start MSFS and wait for live SimConnect aircraft telemetry. Start the research mission. Fly within prepared coverage with valid AGL and reasonable attitude. Only valid flight-time samples become products.

## Fly without a plan

Enable a supported remote instrument and scientific access. With background retrieval allowed and fresh aircraft telemetry, AeroScience requests a small approximately 0.02° cell around the current aircraft position. It does not download a complete region or global dataset. Readiness appears in **Mission Control** and **Science Collection** as well as the planner. A mission must be recording to save scientific observations.

Downloads run independently of the 1 Hz recorder. While data are downloading or unavailable, flight recording continues and the instrument reports the actual missing-data condition. Newly prepared data are used only for subsequent acquisitions; earlier missing flight-time observations are never backfilled as live measurements.

## Read the status honestly

- **Queued / Downloading:** discovery or numeric preparation is in progress; no successful scientific observation is implied. Cancel through the readiness card or Settings, then Retry when appropriate.
- **Ready:** a numeric source is validated and retained. Clouds, nodata, saturation, invalid indices or a footprint extending outside the subset can still prevent a particular acquisition.
- **Limited Coverage:** a prepared subset has no usable preview pixels, a long plan is only partly prepared, or a hyperspectral catalog observation exists whose automatic preparation is unsupported. Read the reason.
- **Unavailable:** no matching source/date/quality, unsupported format, provider error/rate limit, cancellation, insufficient storage, or a refused oversized/incorrect HTTP response. No imagery or numbers are invented. Try a smaller area, another date/quality policy, Retry, or advanced manual import.
- **Offline:** scientific online access/background/network policy prevents retrieval. Previously verified local sources and complete historical products remain usable; no internet is required to review the archive.

## Inspect and keep the results

Open **Science Collection → Remote-Sensing Products → View Data**. Check the numeric view and legend, footprint, native resolution, quality exclusions, dataset/provider/license, source observation time, aircraft position/flight receipt UTC, and processing/retrieval UTC. Atmospheric channels remain simulator-reported values. Satellite and elevation values are independent external observations, often older than the flight.

End the mission, close MSFS and disconnect internet: Mission History, saved telemetry, completed science products and exports remain local. Protect missions/datasets as needed. Automatic cache cleanup never removes original source subsets, observations, completed products or backups to make room.

## Current geographic/product limits

- **Thermal:** Landsat 8/9 Collection 2 **L2SP** temperature, `ST_B10` plus `QA_PIXEL` and `QA_RADSAT`. Historical/clear coverage varies; 30 m delivered pixels are not 30 m native TIRS detail (native thermal resolution is 100 m).
- **Multispectral:** Sentinel-2 L2A B02/B03/B04/B08 and SCL COGs from Earth Search. Exact per-asset scales/additive offsets are preserved; SCL stays on its own grid. No JP2 conversion is required.
- **Terrain:** numeric USGS 3DEP bare-earth composite where available; Copernicus GLO-30 **DSM** fallback includes vegetation/buildings. 3DEP composite dates/native per-pixel resolution/vertical reference are unspecified by this export; Copernicus uses EGM2008. No silent datum mixing or aircraft-MSL subtraction occurs.
- **Hyperspectral:** genuine EMIT, AVIRIS-Classic and AVIRIS-NG catalog discovery only. Protected downloads, NASA login and automated spectral preparation are **not implemented**. No worldwide spectral coverage is claimed. Advanced validated ENVI import remains available.

See [provider details and limits](automatic-data.md), [advanced manual import](sources.md), [storage](storage.md), [exports](exports.md) and [troubleshooting](troubleshooting.md). Actual Windows/MSFS, Quest 3 and physical iPad acceptance remains pending on the gaming PC; genuine-provider cloud acceptance is separate.
