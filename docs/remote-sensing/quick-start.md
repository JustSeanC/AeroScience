# Remote-sensing quick start — v0.3.0 alpha

AeroScience samples **real, imported geographic observations** at positions calculated from your simulated aircraft. It does not expose a live thermal camera, create satellite observations for today, or turn an elevation model into measured LiDAR returns. There are no reference collections in the installer and no automatic worldwide scientific downloads.

## Before flying

1. Install `AeroScience-Setup-v0.3.0.exe`, launch AeroScience from Start and finish its guided storage/Community setup. No terminal or separate server is needed by pilots. Preserve your previous installer and stopped-store backups when testing an alpha upgrade.
2. The dashboard asks about map privacy before any map-provider tile request on a new installation. Choose **Continue with OpenStreetMap** or **Use offline-only maps**. Changing the basemap never supplies scientific source data. Existing installations keep their map choices.
3. Open **Settings → Storage & Data → Storage Management**. Defaults are 25 GB flight / 50 GB science, decimal GB. Choose custom values or Unlimited if appropriate. The Mission Database and Scientific Data Library can be on different local/external drives. An unavailable configured drive is an error, not a reason to create an empty replacement archive.
4. Obtain a bounded, licensed regional collection following [sources.md](sources.md). For a first exercise, use a small WGS84 GeoTIFF elevation subset and [terrain.md](terrain.md). Keep the source's metadata, dates and license. Other supported workflows are [thermal.md](thermal.md), [multispectral.md](multispectral.md) and [hyperspectral.md](hyperspectral.md).
5. Prepare the collection's `source.json` metadata manifest next to its actual data files. This is scientific input metadata, not an application configuration file. The guides give its schema and examples. Use a text editor if the data distributor does not supply this manifest. A JSON manifest alone contains no scientific pixels.
6. In the Windows app's **Settings → Scientific Data Sources**, click **Import scientific source collection…**, choose that manifest and review the source/date/license/copy size/free-space confirmation. AeroScience decodes the files, copies and hashes them into its Science Library, and leaves your originals untouched. Wait for completion. No terminal, network credential, or local-server command is required for this import.
7. In dashboard **Settings → Scientific Data Sources**, refresh and check a latitude/longitude inside the source. Inspect file availability, native band grids, CRS, pixel sizes, scene ID and original observation date. Center coverage is a preliminary check; an entire tilted footprint may extend outside it.

## Record and inspect

1. Open Mission Planner; create a route/survey area inside that source. Choose Universal (default) or Realistic, enable the matching instrument and select its acquisition interval. Save a preset if useful. Both modes retain existing behavior; advanced calibration and airframe restrictions are deferred.
2. Use **Estimate scientific storage**. This estimates bounded 24×24 sampled products and JSON/index/temporary overhead from interval, band count and duration. Original import bytes are additional. It is not a prediction for future full-resolution cameras or LAS data.
3. Start MSFS 2024 and wait for **SimConnect — live aircraft**. Start the mission. The Mission tab records flight data; the toolbar's **Payload** tab exposes the same instrument and science Pause/Resume state. Use your actual aircraft; there is no research-aircraft whitelist.
4. Fly a short, reasonably level pass over the imported area with valid AGL/attitude. The four remote instruments use approximate nadir geometry (terrain uses a narrow swath). Excessive tilt, missing telemetry, cloud masking, missing source files or out-of-coverage locations produce **Scientific data unavailable for this location or acquisition**, not generated substitute values.
5. Open **Science Collection → Remote-Sensing Products → View Data**. Check the footprint/sample map, numeric legend/spectrum/profile, source and flight dates, quality exclusions and native resolution. **Science Pause** stops acquisition independently of flight recording; Resume does not manufacture missed observations.
6. End the mission. In Mission History open that mission's science products after MSFS closes or the internet disconnects. Imported sources and completed products are local. Use **Protect Mission** / **Protect Dataset** to exclude associated previews from cache cleanup.
7. Export JSON/CSV/GeoJSON/GPX/KML, compact evidence or a labeled PNG preview as appropriate; see [exports.md](exports.md). Keep canonical JSON for reproducible review.

## Interpret honestly

- **Simulator reported:** aircraft position, altitude/AGL, attitude, speeds/Mach and supported atmospheric fields from the existing SimConnect provider. Packet timestamps are host UTC at receipt, not a newly claimed simulator clock.
- **External observation:** original Landsat/Sentinel/ENVI/DEM values and original dates, possibly years before this flight.
- **Derived:** footprint geometry, scaled temperatures/reflectance, NDVI, profiles and statistics calculated from those inputs.
- **Modeled:** no modeled scientific sensor generator is supplied in this release. Automated test fixtures are synthetic and identified as such; they are not distributable reference data or MSFS verification.

The browser-fixture screenshots in the completion report illustrate implemented interfaces only. Windows/MSFS, VR and physical iPad acceptance must be performed on the actual gaming PC. See [troubleshooting.md](troubleshooting.md).
