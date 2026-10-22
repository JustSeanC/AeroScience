# Scientific mission operations

Production v0.4.2 remains the QA baseline. Development v0.5.0 adds multi-session collection,
operating assessments and evidence-based grading, plus optional
[user-defined scalar derivations and calibration provenance](instrument-calibration.md).
The [tracker](roadmap/v0.5.0-development-tracker.md) distinguishes implemented controls,
technical validation and pending installed/device/scientific-source acceptance.

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

## Live Sensor Operating Status (v0.5.0 development)

Open **Instruments & Collection** for instrument-first cards, or use the compact **Sensor
Operating Status** panel in Mission Control. Each enabled instrument shows its reference
profile/version, collection phase/session, operating assessment, advice, separate scientific
data status and last assessment time. Disabled instruments are collapsed separately.
**View Technical Details** retains the exact P04 values, units, rule outcomes, derived validity,
source QA, explanations, configuration revision, calibration provenance and evidence hashes.
These are supported checks, not physical hardware-health or accuracy certification.

The shared `operating-ui-1` projection uses typed rule IDs/revisions and outcomes; it never
parses explanations to make a decision. Its deterministic mapping is:

| State or rule | Pilot-facing interpretation |
|---|---|
| Disabled; no flight; Armed/Paused/BetweenSessions/Stopped | Disabled or Not Collecting takes precedence over earlier operating results. End Data closes a session; another session needs its own evidence. |
| Current assessment eligibility | Mission, session, effective configuration content/ID and policy/rule versions must match. Current and recorded position/assessment timestamps must be within 6 seconds, with at most 2 seconds future tolerance; valid, unpaused telemetry must be connected. Assessment must follow the latest collection transition and effective configuration. Missing required rules, stale or unsupported evidence is Cannot Assess. |
| `geometry.ground-intersection` | Supported compliance with supported derived geometry is a checked result. Invalid geometry is Caution; missing AGL/evidence is Cannot Assess. MSL is not substituted for AGL. |
| `planning.forward-overlap`, `planning.side-overlap` | Compare only emitted applicable P04 rules with the saved target/track spacing. Concerns suggest reviewing planned interval, groundspeed, spacing and modeled swath; no guaranteed valid-pixel coverage. |
| `cadence.spacing` | A typed Caution identifies observed acquisition delays relative to configured cadence. Unknown is descriptive spacing, not a passed cadence or detector-sampling check; this limitation stays visible. |
| `riegl.conditional-range` | Below the documented 100 m modeled slant-range minimum is Caution. Other range outcomes remain Unknown; no universal maximum altitude/range is inferred. |
| `environment.payload-temperature` | Applicable only when the selected profile supplies a documented temperature envelope. Missing internal temperature is unassessed, never a failure; ambient temperature is not a substitute. |
| `aviris-ng.nominal-line-rate` | Only the configured nominal 10–100 lines/s reference check is presented, not measured detector performance. |
| Other P04 rows | MSL/orientation are recorded inputs to geometry, not separate invented attitude limits. Missing overlap targets, calibration-derived rows and simulator-channel availability do not create hardware operating passes. Unknown rule IDs/revisions fail safely as Cannot Assess. |

An applicable concern is **Caution**, with remaining uncertainties visible. Otherwise any
applicable unassessed check is **Cannot Assess**. **Within Checked Limits** requires at least
one supported applicable check and no applicable unknown/failed checks. A generic profile may
only support geometry; its absent detector/environmental envelope and unassessed cadence remain
explicit. No positive result is inferred merely from an absence of errors. Guidance supports
planning/configuration review and never directs an unsafe maneuver.

**Scientific Data** is independent: Ready requires fresh supported P04 source QA, Partially
Available indicates mixed/limited QA, Unavailable indicates assessed missing/invalid source
support, Pending indicates an actual waiting acquisition, and Unknown indicates insufficient
evidence. Atmospheric channel timestamps are checked independently of fresh aircraft position.
An unavailable selected Library is reported separately from retained source QA and operating
conditions. Local dataset presence or provider/scene outlines alone never produce Ready. Ready does not
prove an observation was saved or guarantee valid pixels everywhere. Historical Landsat is
not live FLIR imagery; DEM/DSM values are not RIEGL laser returns.

Pausing or changing sessions/configurations invalidates earlier live operating clearance.
No source downloads, geometry processing, archive writes or grading changes are triggered by
reading this panel. Existing refreshes are reused; a one-shot expiry removes favorable live
labels if the connection stalls. Cards update without modal interruptions or focus stealing.
Archived operating reports retain their original records and historical timestamps.

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
schema 3 adds durable approved Library jobs with its own verified backup. Legacy records do not acquire invented sessions, calibration or
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
