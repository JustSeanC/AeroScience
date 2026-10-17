# Science Mission Operations

## Planning and acquisition

Mission Planner stores optional reusable plans separately from recorded missions. Four built-in templates seed editable objectives and instruments. An intended aircraft is descriptive, never a compatibility restriction. Waypoints support reorder/edit/remove, optional target altitude/instructions/objective; survey areas store polygon vertices. General recording remains immediately available from Mission Control or the toolbar without a plan.

Starting a saved plan copies its full JSON snapshot into the new recording in the mission-creation transaction. Editing/deleting the saved plan cannot rewrite that snapshot. The selected Universal/Realistic mode is retained. Advanced calibration/limits and instrument presets remain deferred. Instrument changes during a recording update its current configuration and produce events; the initial plan snapshot remains immutable.

The modular catalog supplies four implementations: ambient thermometer, pressure, wind (two measurements) and density. Instrument configurations select enabled state and a 1–60-second interval independently of flight recording. The current SDK source requests at most 1 Hz. Stable acquisition slots with at most 100 ms phase tolerance avoid halving a nominal 1 Hz source because of small delivery jitter. Duplicate/out-of-order packets are ignored, actual receipt timestamps are retained, missed slots are counted, and no intermediate observations are fabricated. Paused/unavailable packets do not become valid observations.

Each saved reading has a dataset/mission/instrument identity, UTC receipt time, measurement/value/unit, provenance/source and quality. Aircraft position/MSL altitude is optional and included only when its timestamp is within three seconds of the atmosphere packet; the position's own timestamp is retained. There is no claim of perfectly simultaneous packets. Nonfinite/missing channels are rejected independently. A valid reading after a gap is labeled `acquisitionGap`; its number is still the actual reported value. Counts, rejections, missing intervals, last success and achieved intervals are persisted for historical review. Connection events and changed science errors/recovery are recorded without repeating identical errors every update.

`SimulatorReported`, `Derived` and `InstrumentModeled` are distinct provenance categories. This release produces only simulator-reported atmospheric instruments. Test-only fixtures explicitly identify their source as synthetic; they are not included in the distribution. See [SDK definitions and limitations](sdk-research-v0.2.0.md).

## Independent local stores

Mission DB schema **4** adds plans, immutable recording context, markers, notes and dataset availability. Existing telemetry JSON is retained byte-for-byte during migrations. Schemas 1/2/3 receive verified SQLite backups in `Backups` before upgrading. Older Mach/plan/guidance/science fields remain absent or null rather than invented. A newer schema is rejected.

The independent Science Library stores real atmospheric rows in `Observations/science.sqlite` (schema 1), with dataset metadata and indexed timelines. No science database/observations are created just by visiting the dashboard. Its library ID and relative database reference survive moves between drives. A `.science-storage.json` marker distinguishes a previously initialized missing database from a genuinely new library, preventing a deleted/unavailable store from being silently replaced. Science write errors do not stop otherwise valid flight recording; the UI warns and counts rejected readings. Restoring capacity/availability allows acquisition to resume without filling the lost period.

The existing stopped-engine migration framework now also carries a populated library. Services are stopped and file leases acquired before copying, fingerprinting and switching settings. Originals and rollback journals remain. Network storage and reparse points remain unsupported. Settings/configuration stay independent of both selected data locations. An unavailable Science Library does not prevent reviewing the flight archive; its linked datasets are explicitly unavailable.

## Offline maps and review

The map is a self-contained north-up **WGS84 coordinate grid**, with no online tiles, basemap/geocoding requests or cloud data uploads. It displays planned routes/waypoints/polygons, actual track/current aircraft heading, observation markers and available scientific sample positions. Pointer pan, zoom/fit and click-to-add planner waypoints work in the browser. It is a geographic operations view, not satellite imagery or a navigation-certified chart. Longitude wrapping supports routes crossing the antimeridian.

Stored coordinates are full-precision. Overview queries bound display points, retain track endpoints and identify display reduction. Live updates use a sequence cursor after the initial overview rather than rebuilding the full history each second. Browser track/science buffers are bounded. Scientific plots use saved actual points and explicitly labeled units/time/altitude; missing values are not interpolated. Historical observations, notes and markers are paged. Complete exports have no visualization row cap.

Research review shows template/objective/mode, plan snapshot, instrument settings/diagnostics, flight/Mach statistics, science counts/ranges/means, markers/notes, provenance and availability. Reports are reproducible from saved rows and produce no speculative findings or language-model conclusions.

## Estimate before acquisition

In the planner select duration/configuration and choose **Estimate scientific storage**. For each enabled instrument:

`observations = ceil(duration_minutes × 60000 / interval_ms) × channel_count`

The estimated row allocation uses the actual serialized observation model plus 256 bytes for duplicated SQL keys/value columns/index overhead, then a 2× allocation/variable-length margin. Add eight SQLite schema pages and two pages per dataset, at 4096 bytes/page. Zero enabled channels estimate zero scientific data. The response reports the actual resulting bytes/row, per-instrument counts, free destination capacity and a warning if estimated bytes plus the existing 64 MiB safety reserve exceed free space. Starting a saved plan also checks this warning. Actual write-capacity protection remains independent.

This estimates **science observations only**, not flight telemetry, logs, backups or unknown future imagery/instrument formats. It is a warning/estimate, not reserved space or a calibrated growth forecast. No duration means no total estimate. Missing/unavailable drives give unknown capacity, not fictitious free space.

## Protected independent clearing

Open native Windows **Settings → Storage → Data Management**. The dashboard can request opening that local window only from loopback with the normal session/origin/CSRF protections. It cannot submit an erase command; no flight/science deletion HTTP endpoint exists. Paired iPads cannot open native desktop actions. Toolbar commands contain no erase operation.

- **Clear Flight / Mission Data:** removes recorded missions, telemetry, events, markers, recording notes, instrument diagnostics and mission-side links. Saved plans/preflight notes are kept by default; a separate visible checkbox includes them. The Science Library and its original dataset metadata remain independently browsable.
- **Clear Scientific Data:** clears AeroScience-managed observations and dataset metadata from the current atmospheric SQLite store. Flight history, Mach, events, markers, notes, plans, settings, library identity/folders and unrelated/unregistered files remain. Retained mission links resolve as unavailable. Future unimplemented remote-sensing files are not guessed at or recursively deleted.

Both previews show selected paths, counts, managed database size and exact removes/preserves scope. They offer per-mission exports beforehand and an enabled-by-default verified SQLite backup to a chosen folder. Exact phrases `DELETE FLIGHT DATA` / `DELETE SCIENCE DATA` and a final native confirmation are required. Cancellation does nothing. Recording/maintenance guards reject active operations; services stop and exclusive leases protect cross-process writes. SQLite backups include committed WAL content and are checked with integrity/foreign-key checks plus logical fingerprints before clearing. A backup failure stops deletion. No overwrite is allowed for backups.

Deletion is a SQLite transaction, not a recursive folder removal. Failure before commit rolls back rows. Science-reference availability is derived from actual dataset existence and reconciled at startup, so a stop after a committed science clear cannot make missing datasets appear valid. A failed reference reconciliation does not delete flight history. SQLite may retain its allocated file size after clearing; the preview explains this. Legacy scientific rows inside a mission DB block flight clearing to avoid silently destroying the independent scientific archive. Normal uninstall still retains user data.

## Exports

Completed missions offer full versioned JSON (`formatVersion: 2`), flight CSV, science/atmospheric CSV, event CSV, GPX, KML and a UTF-8 research summary. JSON preserves existing mission/telemetry/events fields and adds units, planning context, science observations, diagnostics, notes and markers. CSV headers give units and UTC timestamps; text cells guard against spreadsheet formula injection. Missing Mach/targets/locations stay empty/null.

GPX writes latitude/longitude, UTC time and elevation converted from simulator MSL feet to metres; gaps over 65 seconds start a new segment (recording intervals can be up to 60 seconds). KML writes **longitude,latitude,metres**, actual absolute-MSL tracks/points and clamped horizontal planned routes/polygons. No ellipsoid/MSL conversion is invented. All formats stream actual records with bounded buffers; no 1,000/5,000-row display cutoff is reused for export. Text summary statistics are SQL-derived, with complete streamed notes and markers.
