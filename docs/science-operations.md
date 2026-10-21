# Scientific mission operations

These instructions describe the implemented v0.4.2 workflow. The v0.5.0 session,
operating-assessment and grading lifecycles remain under development. P03 adds optional
[user-defined scalar derivations and calibration provenance](instrument-calibration.md); consult
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

## Scientific collection sessions (v0.5.0 development)

Mission Control and Science Collection share **Mission intelligence**. The flight name,
mode, aircraft and recording time remain separate from the current science session's
stable identity, sequence, phase and time. Expand the instrument, performance, frozen
objectives and automation sections for supporting evidence. Keyboard focus and open
sections remain in place during polling. Controls are disabled when their authoritative
state is unavailable; reconnect before issuing a new deliberate command.

Manual controls use session-bound durable request identities. A transport retry repeats
the identical request. The optional control revision tolerates newly recorded telemetry
only; another control, configuration change or continuity hold invalidates it. A stale
End cannot be redirected to another session. Existing clients retain strict revision checks.

Live original atmospheric Good Data uses verified scalar acquisition receipts: wind speed
and direction count as two values in one set, and calibration derivatives are excluded.
Pending remote QA, native pixel unions and intended-work completeness remain unassessed
where the background evaluator has no supported result. Provisional bounds are not a final
letter grade. Whole-flight obligations survive all session boundaries. Detailed archived
reports retain session diagnostics, source dates, hashes and report revisions; opening
History never triggers recalculation. The original collection audit is paged separately.

Library links open the existing geographic browse/download and NASA guided workflows.
Historical satellite/airborne source imports are not new simulator-aircraft observations.
EMIT remains discovery/official access only. Protected Earthdata retrieval and genuine
AVIRIS-family numeric import acceptance remain deferred to P19; no login is required here.

Flight recording and science collection have separate lifecycles. On Mission Control,
choose **Arm science without starting collection** to record the departure/transit
without taking scientific samples. On Science Collection use **Start new science
session**, **Pause data**, **Resume data**, and **End current science session**. Pausing
keeps the same session; ending preserves it and allows a subsequent new session. Use
**End mission** separately to finish the flight. No permanent flight-wide Finish Science
action is provided.

Manual collection commands take authority over automation. Explicit **Rearm automatic
collection** permits future configured actions; it does not start/resume a session or
replay missed actions. Commands are bound to the displayed flight and session. A changed
session requires synchronization and a new deliberate command; an old End cannot close a
new session. A storage hold requires resolving storage and explicit rearm. Flight recording
continues independently while its database remains writable.

History's **Science sessions and retained measurements** lists stable session identities,
start/end information and retained original/derived values and remote product counts.
These counts are not scientific accuracy, a valid-pixel coverage calculation or a grade.
JSON and scientific CSV exports retain session/acquisition identities; older archives
remain unsegmented rather than acquiring invented sessions. Restart interrupts the open
session and never resumes flight recording or collection automatically.

### Optional geographic collection automation

In Mission Planner expand **Scientific collection automation**. Choose Armed or Manual,
add explicit waypoint/gate or survey-entry/exit actions, and give planned sessions distinct
keys such as A and B. A gate direction is **degrees true**; tolerance and hysteresis are
shown in metres. Pass numbers distinguish return visits to the same target. Automatic
End is optional and requires an explicit waypoint or directional gate. Survey exits never
end sessions; optional boundary pause/resume retains the same session.

After geographic edits, use **Refresh and review automation targets**, then **Save mission
plan**. This freezes stable target bindings; changing a polygon invalidates the old binding
until explicitly reviewed. No collection or downloads start during review. Select the
saved plan before starting the flight. Armed policy waits for a configured Start; manual
commands take precedence, and manual End requires explicit rearm for future automatic
Starts. Manual Resume deliberately overrides scoped route/boundary pauses; a continuity
hold still requires rearm first.

Triggers run in the companion backend with every display closed. Point actions require an
actual observed proximity; gates require continuous bracketing packets in the configured
direction. Missed points are not invented. Duplicate packets, gaps, stale/out-of-order
positions, reconnects and implausible jumps cannot fire skipped actions. A continuity
hold pauses scientific collection and requires explicit rearm; the next packet establishes
a new baseline. Default area entry requires a witnessed entry; **Start when initially
inside** is a separate explicit permission. Boundary regions form a union, with dwell
and distance hysteresis suppressing grazes. Transit or recorded track is not valid-pixel
scientific coverage.

Automation supports local bounded polygons (up to 256 vertices per region, 1,024 total,
within 100 km and below 80° latitude). Dateline membership/gates retain WGS84 coordinates.
Unsupported geometry is rejected with manual collection available. Plans permit 128
explicit actions; up to 4,096 flight-wide automatic receipts are retained without eviction.
If that technical receipt budget is reached, automation holds visibly and manual controls
remain available. Automatic End takes priority over coincident Start/Resume, and Pause over
coincident Resume. A later distinct Start can create another session after an automatic
End. No autopilot/aircraft control is sent to MSFS.

The current archived telemetry has no separate slew-active flag. Paused positions and
implausible jumps are detected; a slow repositioning that looks continuous cannot be
identified as slew from position evidence alone. Use manual Pause before simulator slew
and explicitly rearm afterward. Actual MSFS pause/slew/reconnect behavior remains a required
installed acceptance check.
