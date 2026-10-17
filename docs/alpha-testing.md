# v0.1.1-alpha implementation and acceptance

The implementation is available in source. The Windows app cross-compiles/publishes on Linux; its graphical execution, real-drive migrations and new Mach reception have not been tested on Windows. Public distribution is now authorized, but no complete alpha release is published yet. The first release uses a local Windows build and manual website upload, with no runner required; see [the Windows release guide](public-release-windows.md). Earlier v0.1.0 evidence includes actual T-38N live data and working native toolbar movement/resizing; it does not validate these new changes.

## Ordinary user workflow

From a complete, validated alpha download, extract to a new folder and open **AeroScience.exe**. It starts the local engine/dashboard, enables the toolbar bridge and opens the browser. Start MSFS before or afterward; the app retries the connection automatically. Leave the app running while recording. Closing a browser/toolbar does not stop recording; exiting the app during a mission asks for confirmation and preserves it as interrupted.

No user PowerShell, batch launchers, environment variables, typed localhost addresses, developer tools or cloud account are required. The source contains maintainer build tools; those are not tester instructions. Production generated telemetry is removed. Legacy simulated missions remain labeled and readable.

The **Settings** button opens a native Windows dialog. Its General & Toolbar tab contains recording interval, optional LAN/IP selection, Community discovery/browse/install and an official-runtime import for personal developer testing. A complete alpha must provide a permitted runtime distribution/prerequisite route; importing a personal SDK DLL is not evidence that the tester installation problem is resolved. Local operation uses loopback; LAN is optional. Permit the app on the Windows Private network profile only when enabling private LAN. The pairing code is available in the authenticated PC dashboard Settings.

## Offline archive and export

Open the same app with MSFS closed and internet disconnected. Mission History must still list all archived missions, page through them, load details/events and show saved telemetry. Live controls show unavailable/waiting; starting a new live mission is rejected. Reconnecting MSFS restores telemetry independently of archive access.

For a completed/interrupted mission, choose **View mission → Export complete mission (JSON)**. The download contains versioned metadata, science mode/source, UTC timestamps, all events, stable dataset references and all telemetry including nullable `machNumber`. This is not the 5,000-sample preview link. Exports do not alter originals; an active mission must be ended before a consistent complete export.

## Settings → Storage

Two separate sections manage **Mission Database** and **Scientific Data Library**. Each shows its folder, used bytes, available space, Browse / Change Location, Open Folder and Restore Default. Settings persist in `%LOCALAPPDATA%\AeroScience\settings.json` independently of movable stores. No manual editing is needed.

Defaults:

- Mission database: `%LOCALAPPDATA%\AeroScience\missions.db`.
- Scientific library: `%LOCALAPPDATA%\AeroScience\ScienceLibrary`.

The science library has an actual identity manifest and empty organizational directories for imagery, elevation, observations, reports and exports. No placeholder observations, sensor products or mapping features are generated. `dataset_references` uses stable dataset/library GUIDs and relative paths; moving a library retains its identity and references. The library can be on another internal/external drive. Missing science storage does not prevent access to an available mission archive.

The required large-data warning appears at initial setup and library selection. Capacity is reported without forecasting unimplemented science formats/rates. A library below 1 GiB triggers an advisory capacity warning; this is a policy threshold, not a data-size estimate. Recording/migration uses a 64 MiB safety reserve in addition to estimated copy size. Existing observations/missions are never automatically deleted to free space.

### Migration and recovery

1. End the active mission. The app atomically blocks new starts, stops local services and releases storage before migrating.
2. Choose a destination through Browse Folder and confirm. The SQLite path uses SQLite's backup API, not copies of a live DB/WAL. The library uses verified file copies. Both validate local paths, permissions, space and destination conflicts, and hold exclusive leases.
3. The dialog shows progress. Verification precedes the atomic settings switch. Database integrity/foreign keys and logical table contents are compared; library paths/content hashes are compared. Empty directories, IDs, modes, source labels and references are retained.
4. Originals remain at their old locations after success. The app does not clean them up automatically. Existing destination stores are never merged/overwritten: choose another empty folder or explicitly open the existing archive/library. Restoring Default may select an older original archive; the confirmation explains that it does not merge newer records into it.
5. If interrupted, a local journal identifies the source/destination and verification stage. Startup retains the original selection unless an activated destination verifies. Partial copies remain for review. Settings → Storage can explicitly restore the original setting from an interrupted migration; both copies are retained.

Unavailable external storage produces a clear error. The app never silently creates a replacement archive. Reconnect the drive or explicitly select a verified existing archive through Storage. Network drives, UNC paths and symbolic-link/junction storage are unsupported in this alpha. Data belongs outside the application installation folder.

Schema 1/2 archives receive verified SQLite backups under their mission folder's `Backups` before upgrading to schema 3. Schema 3 adds portable dataset references; Mach is an optional field in existing JSON samples. Earlier modes/source labels/IDs/timestamps/payloads are retained. The old v0.1.0 executable refuses schema 3, so use the retained backup to roll back rather than pointing it at the upgraded file.

## Mach and SDK contract

The official SDK documents [`AIRSPEED MACH`](https://docs.flightsimulator.com/msfs2024/html/6_Programming_APIs/SimVars/Aircraft_SimVars/Aircraft_Misc_Variables.htm#AIRSPEED%20MACH) as current Mach in Mach units. The client requests it as FLOAT64 with unit `mach`; it is not derived from IAS. Nonfinite/negative values become null without discarding otherwise valid flight data. Dashboard and toolbar show two decimals; old recordings show unavailable.

The managed payload is now 360 bytes: 256-byte TITLE, the unchanged original 12 doubles (offsets 256 through 344), and Mach at 352. The SDK message's fixed payload offset remains 40; a complete new payload needs 400 bytes. Managed tests assert every field offset, count, decode and serialization. The official-SDK C++ contract additionally asserts the packed client layout while retaining the previously corrected CommBus member-offset checks. Compilation against the actual Windows SDK, real Mach reception and aircraft-reference comparison remain pending.

## Sampling investigation

The prior algorithm accepted a frame only when at least 1,000 ms had elapsed since the *last accepted* frame. A deterministic 120-update sequence alternating 995/1005 ms intervals reproduces 60 committed samples under that rule. This establishes a code defect capable of halving nominal 1 Hz data; it does not prove every cause of the earlier 47-sample/80-second observation.

The recorder now assigns actual observations to a stable schedule, with a bounded 10% phase tolerance capped at 100 ms. It accepts at most one observation per slot, preserves the received UTC timestamp, rejects duplicate/backward/paused observations and never fills missing slots. The same regression records all 120 observations, unchanged, and counts a deliberate later gap correctly.

The dashboard distinguishes configured target, sample cadence over the first/last committed observation, mission-wide average including initial delay/gaps, committed sample count and missing intervals. Snapshot diagnostics also include received/skipped counts, first/last sample timestamps and initial sample delay. Missing intervals are counted when data resumes; waiting/paused state remains explicit meanwhile. The SDK requests one update/second, so the graphical recording interval starts at 1,000 ms; slower rates are supported without upsampling.

## Gaming-PC test record — not yet tested for this build

Use Passed/Failed/Blocked/Not yet tested with app/commit/MSFS/SDK versions and actual evidence. The SDK installer version is still unknown; game `1.9.18.0`/validator `1.0.7.0` are not that version.

1. Launch AeroScience.exe with MSFS/internet off, inspect an existing v0.1.0 archive, export a complete mission and restart. Confirm no invented flight data and no terminal/server setup.
2. Connect MSFS afterward. With an appropriate aircraft, compare Mach against the simulator's Mach indication/reference, including unavailable/low-speed cases. Check existing flight fields, units/signs and freshness. Never use IAS alone as the Mach reference.
3. Record sustained stable flight, turns, climb/descent and high speed. Compare actual UTC intervals, received/committed counts, target/cadence/mission-average rates and explicit gaps. Repeat pause/resume, load/aircraft changes and disconnect/reconnect.
4. Complete Universal and Realistic missions, starting/ending from opposite displays. Close/reopen displays while recording. End, inspect/export, exit/relaunch and verify retained modes, sources, samples, Mach and history. Test default/third-party fixed-wing and rotary/hover examples without assuming wider compatibility.
5. Move the database and library independently to different real drives, then restart offline. Check source copies, complete data, library identity/references and restore-default conflict options. Test low capacity, access denial, destination conflicts, disconnection and interruption/recovery. Do not forcefully unplug the only valuable original copy; use backed-up test archives.
6. Install/update the genuine toolbar through Settings with MSFS closed. Verify correct Community discovery or Browse selection, one package copy, retained unrelated add-ons and backup/rollback. Open in MSFS; test all native controls/edges, scrolling, narrow layouts, Mach, navigation and mission commands. Preserve `/JS/dataStorage.js`; no custom dragging.
7. Verify private-LAN pairing/history/live controls on physical iPad Safari. Measure Quest 3 FPS/frame times/stutters/resources under consistent app-stopped, panel-closed and panel-open conditions. No extra VR interaction matrix or Compact VR is introduced.
8. Test a clean Windows account/machine and the complete permitted runtime route. Use in-app Collect Feedback; review its local ZIP and confirm no DB, SDK DLL or pairing secret. Report the exact URL/status for the earlier unidentified debugger resource error.

## Local release and minimum acceptance

Maintainers use [Build-ReleaseCandidate.ps1](../scripts/release/Build-ReleaseCandidate.ps1) on the gaming PC with its existing official SDK. The app root includes `Toolbar/aeroscience-toolbar` and `Toolbar/sdk-build.json` for graphical installation. Native runtime permission/loading, clean matching source provenance, SDK compilation/structural validation and file hashes remain required.

Test the **extracted complete candidate ZIP**, then fill its generated acceptance record. The minimum prerelease checks are real Windows app/dashboard startup, offline archive/details/export, graphical toolbar installation/loading, live SimConnect, a short mission retained after restart, supported runtime dependencies and extracted package integrity/privacy. The seven checks require genuine evidence tied to the exact commit and payload fingerprint. The old v0.1.0 screenshots and portable fixtures cannot establish them.

Extended aircraft variety, migration failures, sustained cadence, live Mach, iPad and VR tests may remain pending for this feedback alpha. Keep those limitations visible. Stable releases require extended checks. Finalization verifies the accepted bytes without rebuilding and produces the final platform-named ZIP, checksum and generated notes. A maintainer uploads those through GitHub Releases; testers need no Actions artifact access.

The optional Actions workflow can later prepare candidates on a trusted SDK Windows runner and publish a tested earlier candidate with the standard same-repository token. It is not needed for this first release. No private destination or special private-release token is required. See [release infrastructure](release.md) and [native runtime permission status](runtime-distribution.md).
