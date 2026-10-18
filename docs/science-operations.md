# Science Mission Operations

## Planning and acquisition

Mission Planner stores optional reusable plans separately from recorded missions. Four built-in templates seed editable objectives and instruments. An intended aircraft is descriptive, never a compatibility restriction. Waypoints support reorder/edit/remove, optional target altitude/instructions/objective; survey areas store polygon vertices. General recording remains immediately available from Mission Control or the toolbar without a plan.

Starting a saved plan copies its full JSON snapshot into the new recording in the mission-creation transaction. Editing/deleting the saved plan cannot rewrite that snapshot. The selected Universal/Realistic mode is retained. Advanced calibration/limits remain deferred; reusable instrument presets are available in v0.2.1. Instrument changes during a recording update its current configuration and produce events; the initial plan snapshot remains immutable.

The modular catalog supplies four implementations: ambient thermometer, pressure, wind (two measurements) and density. Instrument configurations select enabled state and a 1–60-second interval independently of flight recording. The current SDK source requests at most 1 Hz. Stable acquisition slots with at most 100 ms phase tolerance avoid halving a nominal 1 Hz source because of small delivery jitter. Duplicate/out-of-order packets are ignored, actual receipt timestamps are retained, missed slots are counted, and no intermediate observations are fabricated. Paused/unavailable packets do not become valid observations.

Each saved reading has a dataset/mission/instrument identity, UTC receipt time, measurement/value/unit, provenance/source and quality. Aircraft position/MSL altitude is optional and included only when its timestamp is within three seconds of the atmosphere packet; the position's own timestamp is retained. There is no claim of perfectly simultaneous packets. Nonfinite/missing channels are rejected independently. A valid reading after a gap is labeled `acquisitionGap`; its number is still the actual reported value. Counts, rejections, missing intervals, last success and achieved intervals are persisted for historical review. Connection events and changed science errors/recovery are recorded without repeating identical errors every update.

`SimulatorReported`, `Derived` and `InstrumentModeled` are distinct provenance categories. This release produces only simulator-reported atmospheric instruments. Test-only fixtures explicitly identify their source as synthetic; they are not included in the distribution. See [SDK definitions and limitations](sdk-research-v0.2.0.md).

## Independent local stores

Mission DB schema **5** retains plans, immutable recording context, markers, notes and dataset availability, and adds independent instrument presets and saved analysis settings. Existing telemetry JSON is retained byte-for-byte during migrations. Schemas 1/2/3/4 receive verified SQLite backups in `Backups` before upgrading. Older Mach/plan/guidance/science fields remain absent or null rather than invented. A newer schema is rejected.

The independent Science Library stores real atmospheric rows in `Observations/science.sqlite` (schema 1), with dataset metadata and indexed timelines. No science database/observations are created just by visiting the dashboard. Its library ID and relative database reference survive moves between drives. A `.science-storage.json` marker distinguishes a previously initialized missing database from a genuinely new library, preventing a deleted/unavailable store from being silently replaced. Science write errors do not stop otherwise valid flight recording; the UI warns and counts rejected readings. Restoring capacity/availability allows acquisition to resume without filling the lost period.

The existing stopped-engine migration framework now also carries a populated library. Services are stopped and file leases acquired before copying, fingerprinting and switching settings. Originals and rollback journals remain. Network storage and reparse points remain unsupported. Settings/configuration stay independent of both selected data locations. An unavailable Science Library does not prevent reviewing the flight archive; its linked datasets are explicitly unavailable.

## Offline maps and review

The map uses locally bundled **Leaflet and Natural Earth geography** without required internet, external geocoding or cloud uploads. Online detail tiles are an explicitly disclosed optional choice. It displays planned routes/waypoints/polygons, actual track/current aircraft position, observation markers and available scientific sample positions. Pointer pan, zoom/fit and click-to-add planner waypoints work in the browser. It is a geographic operations view, not satellite imagery or a navigation-certified chart. Longitude wrapping supports routes crossing the antimeridian.

Stored coordinates are full-precision. Overview queries bound display points, retain track endpoints and identify display reduction. Live updates use a sequence cursor after the initial overview rather than rebuilding the full history each second. Browser track/science buffers are bounded. Scientific plots use saved actual points and explicitly labeled units/time/altitude; missing values are not interpolated. Historical observations, notes and markers are paged. Complete exports have no visualization row cap.

Research review shows template/objective/mode, plan snapshot, instrument settings/diagnostics, flight/Mach statistics, science counts/ranges/means, markers/notes, provenance and availability. Reports are reproducible from saved rows and produce no speculative findings or language-model conclusions.

## Estimate before acquisition

In the planner select duration/configuration and choose **Estimate scientific storage**. For each enabled instrument:

`observations = ceil(duration_minutes × 60000 / interval_ms) × channel_count`

The estimated row allocation uses the actual serialized observation model plus 256 bytes for duplicated SQL keys/value columns/index overhead, then a 2× allocation/variable-length margin. Add eight SQLite schema pages and two pages per dataset, at 4096 bytes/page. Zero enabled channels estimate zero scientific data. The response reports the actual resulting bytes/row, per-instrument counts, free destination capacity and a warning if estimated bytes plus the existing 64 MiB safety reserve exceed free space. Starting a saved plan also checks this warning. Actual write-capacity protection remains independent.

This estimates **science observations only**, not flight telemetry, logs, backups or unknown future imagery/instrument formats. It is a warning/estimate, not reserved space or a calibrated growth forecast. No duration means no total estimate. Missing/unavailable drives give unknown capacity, not fictitious free space.

## Protected independent clearing

Open native Windows **Settings → Storage & Data → Data Management**. The dashboard can request opening that local window only from loopback with the normal session/origin/CSRF protections. It cannot submit an erase command; no flight/science deletion HTTP endpoint exists. Paired iPads cannot open native desktop actions. Toolbar commands contain no erase operation.

- **Clear Flight / Mission Data:** removes recorded missions, telemetry, events, markers, recording notes, instrument diagnostics and mission-side links. Saved plans/preflight notes are kept by default; a separate visible checkbox includes them. The Science Library and its original dataset metadata remain independently browsable.
- **Clear Scientific Data:** clears AeroScience-managed observations and dataset metadata from the current atmospheric SQLite store. Flight history, Mach, events, markers, notes, plans, settings, library identity/folders and unrelated/unregistered files remain. Retained mission links resolve as unavailable. Future unimplemented remote-sensing files are not guessed at or recursively deleted.

Both previews show selected paths, counts, managed database size and exact removes/preserves scope. They offer per-mission exports beforehand and permanent deletion without a new backup by default, with verified SQLite backup to a separate chosen folder as an explicit alternative. Exact phrases `DELETE FLIGHT DATA` / `DELETE SCIENCE DATA` and a final native confirmation are required. Cancellation does nothing. Recording/maintenance guards reject active operations; services stop and exclusive leases protect cross-process writes. SQLite backups include committed WAL content and are checked with integrity/foreign-key checks plus logical fingerprints before clearing. A backup failure stops deletion. No overwrite is allowed for backups.

Deletion is a SQLite transaction, not a recursive folder removal. Failure before commit rolls back rows. Science-reference availability is derived from actual dataset existence and reconciled at startup, so a stop after a committed science clear cannot make missing datasets appear valid. A failed reference reconciliation does not delete flight history. SQLite may retain its allocated file size after clearing; the preview explains this. Legacy scientific rows inside a mission DB block flight clearing to avoid silently destroying the independent scientific archive. Normal uninstall still retains user data.

## Exports

Completed missions offer full versioned JSON (`formatVersion: 2`), flight CSV, science/atmospheric CSV, event CSV, GPX, KML and a UTF-8 research summary. JSON preserves existing mission/telemetry/events fields and adds units, planning context, science observations, diagnostics, notes and markers. CSV headers give units and UTC timestamps; text cells guard against spreadsheet formula injection. Missing Mach/targets/locations stay empty/null.

GPX writes latitude/longitude, UTC time and elevation converted from simulator MSL feet to metres; gaps over 65 seconds start a new segment (recording intervals can be up to 60 seconds). KML writes **longitude,latitude,metres**, actual absolute-MSL tracks/points and clamped horizontal planned routes/polygons. No ellipsoid/MSL conversion is invented. All formats stream actual records with bounded buffers; no 1,000/5,000-row display cutoff is reused for export. Text summary statistics are SQL-derived, with complete streamed notes and markers.

## v0.2.1 Science experience

Use **Science → Science Collection** (the old `#instruments` bookmark still works). Mission Control links directly to Science and shows collection state, enabled instruments, acquisition sets and measurement values. Wind produces two measurement values per acquisition set. **Pause science collection** suspends only scientific saving; aircraft telemetry and mission recording continue. Resume retains current instruments and datasets. Deliberate pause events/durations survive in history/JSON and do not inflate unexpected missing-slot counts. A restarted/interrupted mission never silently resumes.

Science display units are a persistent application preference shared with the toolbar and historical charts. Choose Aviation/Scientific or individual supported overrides; the original simulator values, source units, provenance and default exports are unchanged. Explicit chart display CSV identifies converted units and is a bounded display export, not a replacement for complete canonical mission exports.

Reusable instrument presets contain the current supported instruments/intervals and can be edited/renamed/duplicated/deleted or applied to future/planned/spontaneous missions. Applying one does not change route/aircraft/objective. Unsupported future instrument IDs remain stored with warnings and are never silently enabled. Mission snapshots remain independent. Per-channel advanced instrument settings are not implemented.

The map uses bundled public-domain Natural Earth geography with Leaflet and Leaflet.draw; cities and a non-exhaustive airport gazetteer are searchable without internet. Polygons and waypoints are editable. An unset location remains unset, while explicit 0,0 is valid. Mercator display stops at ±85.0511°; stored WGS84 coordinates are retained. Online OpenStreetMap detail is optional, explicitly disclosed and has no offline-cache/prefetch feature.

Eight patterns use GeographicLib.NET WGS84 geodesics: lawnmower, creeping line, expanding square, racetrack, orbit, box, radial spokes and straight transect. Dimensions use metres, bearings true north, curves use 5° samples; total route distance is reported. Generated waypoints remain editable. Regeneration replaces only a pattern's tagged points; undo restores geometry. No autopilot or aircraft control is written.

Historical comparisons use the immutable plan snapshot, configurable tolerance and gap limit. A waypoint is reached only by an observed point in route order; closest proximity is shown separately. Distance/traversal exclude gaps and reversed timestamps. Deviation uses a local projected candidate measured on a WGS84 geodesic segment; long/polar segments require caution. Polygon path traversal uses approximate 250 m integration. Sensor-area coverage is not established; no arbitrary percentage or pilot score is generated.

Chart.js profiles are scatter plots (altitude can reverse); time plots use original UTC observations without smoothing and split visible long gaps. Display reduction retains bucket minima/maxima; full raw rows remain in canonical exports. Use selected ranges, hover/tap, point toggle, expanded view and PNG export.

Native Data Management has separate **Permanently delete without creating a new backup** and **Create a recovery backup, then delete** choices. The former is selected by default independently in both sections, opens no folder picker or Explorer, and warns that deletion may be irreversible. Cancelling a requested backup picker cancels deletion entirely. Old/migration backups are retained. Preview/typed confirmation and stopped-recording guards remain mandatory; recovery destination must be outside both selected data roots and application installation. Clearing rows may retain SQLite allocation; no automatic compaction or deletion policy is introduced. Plans are kept unless explicitly selected; presets are retained.


## v0.3.0 extension

See [v0.3.0 implementation/verification](v0.3.0-completion.md), [remote-sensing operations](remote-sensing/quick-start.md), [storage safety/accounting](remote-sensing/storage.md) and [current Windows release process](v0.3.0-windows-testing.md). Remote processing is a bounded background worker independent of flight recording; copied scientific sources and complete products use stable identities, relative paths and integrity checks. Existing acquisition/flight modes and stopped-store migrations are retained. Historical manual acceptance instructions in older sections do not apply to the default v0.3.0 automatic technical alpha pipeline.
