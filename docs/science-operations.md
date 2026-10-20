# Scientific mission operations

These instructions describe the implemented v0.4.2 workflow. The v0.5.0 session,
calibration, operating-assessment and grading contracts are under development; consult
the [tracker](roadmap/v0.5.0-development-tracker.md) before treating them as usable controls.

## Plan, prepare and fly

The [Mission Planner](mission-planner.md) saves reusable objectives, routes, survey
polygons, instrument configurations and presets. Aircraft descriptions do not restrict
compatibility. Starting a plan freezes its JSON in the recording transaction; later plan
edits/deletion cannot rewrite that mission. Universal remains the default, with Realistic
retained as mission metadata. No aircraft whitelist or simulator write-control is used.

Enable the instruments needed for the mission. Atmospheric temperature, pressure,
wind and density use available SimConnect channels. Stable independent acquisition
slots tolerate bounded delivery jitter; duplicate/out-of-order/stale/paused inputs are
not filled with invented observations. The source requests at most 1 Hz. Missing channels
are handled independently; position is attached only when sufficiently close in time and
its own timestamp is retained. See [SDK variables and limits](sdk-research-v0.2.0.md).

Thermal, multispectral, terrain and supported hyperspectral products sample independently
observed external geographic sources. Follow the [automatic preparation quick start](remote-sensing/quick-start.md).
Consent is separate from online basemaps. Preparation runs in bounded background work,
respects network/storage controls and reuses verified local sources. Coverage, offline
and QA failures never generate substitute observations. [Advanced import](remote-sensing/sources.md)
remains available for real user-supplied sources; ordinary supported automatic workflows
do not require GIS conversion or handwritten manifests.

Reference profiles model documented geometry and selected settings. They do not confer
hardware calibration or resolution on a satellite scene. Preserve source observation
date, retrieval/processing time, units/scale/offset, grids, QA/nodata, license and original
scientific values. A DEM/DSM is not an aircraft LiDAR return. See
[instrument profiles](v0.4.0-instrument-profiles.md) and [methods](v0.4.0-scientific-methods.md).

## Recording, review and exports

Flight recording and intentional science pause are independent. Pausing science does not
end the recorded flight. Closing a display does not stop the engine. Gaps and failures
remain visible rather than being backfilled as live observations. Existing replay,
coverage, comparisons and reconstructed products retain their evidence limitations.
Flight-path adherence and modeled footprint overlap are not QA-valid scientific coverage.

Mission History and retained science remain accessible offline with MSFS closed, as long
as their selected data drives are available. Complete JSON/CSV/GPX/KML/research-package
exports retain original timestamps, source/mode labels and full stored rows; display
reduction does not reduce exports. Canonical CSV keeps injection protection. Comparisons
require compatible units, native grids/source support and applicable dates; missing
or incompatible evidence is explicit. See [pilot review](v0.4.2-pilot-guide.md),
[replay/coverage](v0.4.0-replay-and-coverage.md) and [comparisons](v0.4.1-scientific-comparison.md).

## Independent storage and safe maintenance

Settings selects the Mission Database and Scientific Data Library independently. Default
caps remain 25 GB / 50 GB; cleanup removes regenerable caches only. Original observations,
completed products, pinned sources, migration/recovery backups and unrelated files are
not automatic cleanup targets. Library IDs and relative dataset paths survive verified
storage moves. Missing selected stores never silently become empty replacements.

Stable v0.4.2 uses mission schema 7 and Library schema 2. Current v0.5.0 development adds
mission schema 8 evidence tables with a verified WAL-aware pre-upgrade backup; Library
schema 2 is unchanged. Legacy records do not acquire invented sessions, calibration or
grades. A v0.4.2 binary must use the preserved compatible backup instead of opening a
migrated database. Migration requires stopped recording, coordinated services/leases,
destination validation, SQLite backup/integrity/fingerprint checks, retained originals
and recovery journals. See [storage management](remote-sensing/storage.md).

Data Management keeps flight and science clearing independent. Permanent deletion without
a new backup is the default and opens no backup picker. Requested backup cancellation
cancels the entire operation. Both typed phrases, final warnings, active-recording guards,
transactions and existing backup preservation remain mandatory. Use disposable copies
for acceptance testing, never the only research archive.
