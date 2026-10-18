# Changelog

## 0.3.0 — Remote Sensing & Research Payloads (public alpha)

- Reuse shared persisted display preferences for Aviation/Metric planning: default NM, feet MSL, knots and NM²; adaptive m/km, meters MSL and metric area when selected. Convert entered values while retaining canonical precision and old explicit preferences. Preserve true headings, meters-based WGS84 geometry, saved patterns and export accuracy.
- Make Track Spacing the primary parallel-survey control; recalculate passes/route/envelope previews, and add heading-aligned geodesic Fit Pattern to Survey Area using the entered spacing. Label envelope estimates as unvalidated sensor coverage; actual-footprint/overlap recommendations remain v0.4.0 work.
- Correct Planner search so blank/no-match queries never move the map and Find lists results for explicit selection. Bundle a hash-pinned public-domain OurAirports snapshot with 72,684 open facilities, exact ICAO/IATA priority, readable names and offline city/coordinate search.
- Add explicit temporary waypoint placement and Save/Cancel, visual survey drawing/undo/finish/name/save with geometry validation and approximate geographic dimensions. Keep unsaved drafts across section changes and warn before discarding; organize the single-page planner into five sections with advanced fields collapsed.
- Repair the existing live pattern preview with request/editor lifetime guards, independent preview geometry, deliberate Fit Preview, map/survey center selection and explanatory heading/step/entry/exit references. Verify lawnmower versus creeping-line direction independently. Show semantic orbit outlines and sparse pins while preserving every 5° export navigation point and explicit compatibility counts/warnings.
- Share selectable route/track/area/aircraft/marker/sample/footprint layers. Clarify Planner, Control and Science roles; make Scientific Sample Locations expandable and distinguish successful atmospheric positions from external-data footprints and missing coverage.
- Coordinate native actions before UI dispatch and track modal ownership so repeated/multi-client requests focus an existing auxiliary dialog, with legitimate children and reliable guard release. Preserve live dashboard access and existing recording/storage protections. Windows focus and uncoached-user acceptance remain pending; larger P2 semantic exports/geocoder/redesign work is explicitly backlogged.

- Make separately consented, direct automatic scientific discovery/preparation the normal terrain, thermal and multispectral workflow: numeric USGS 3DEP, Copernicus GLO-30 DSM fallback, Landsat C2 L2SP ST_B10/QA, and Sentinel-2 L2A reflectance/SCL COG subsets. Generate manifests internally and retain verified original numeric subsets in the existing Scientific Data Library.
- Add scientific readiness to Planner, Control and Science, bounded preflight route/survey cells, background aircraft-area retrieval, privacy/offline/unmetered/budget/concurrency/date-quality controls and cancel/retry. Preserve 1 Hz flight recording, original metadata/units/QA, all existing library/import/storage protections and independent map consent.
- Complete the final genuine online acceptance for USGS 3DEP, Landsat C2 L2, Sentinel-2 L2A and Copernicus GLO-30: actual discovery, bounded numeric retrieval, manifest generation, verified import, QA-aware sampling and saved/reloaded mission products. Independent GDAL reads match all 5,329 GLO-30 crop pixels and 576 sampled elevations exactly. NASA geographic discovery returned six actual observations; protected download/login and automatic hyperspectral preparation remain unsupported. Record source-bound evidence and retain the release gate against future stale/pending results; synthetic flight inputs do not establish Windows/MSFS acceptance.

- Add bounded, asynchronous real-file Landsat thermal, Sentinel multispectral/NDVI, ENVI hyperspectral and DEM-derived terrain sampling with approximate attitude-aware WGS84 footprints, explicit source/flight/processing dates and genuine units/QA/nodata. No fabricated global sensors, live thermal camera or DEM-as-LiDAR claims.
- Add graphical local source import, managed source/product identities and hashes, complete historical viewers, scientific exports, compact local evidence and planning-only future self-hosted campaigns documentation.
- Default fresh installations to OSM/Streets after a privacy/offline-only choice before first external tiles; preserve old map preferences, offline fallback and optional own-key Esri.
- Add configurable 25 GB flight / 50 GB science defaults, physical usage/free-space warnings, independent safe quota suspension/recording-stop reasons, persistent mission/dataset protection and detailed cache-only cleanup preview/results/history. Preserve originals/backups and count interrupted work; retain verified migrations, deliberate clear safeguards and optional stopped-store compaction.
- Add optional cached GitHub update/channel/reminder controls; open official release pages without automatic installer execution.
- Produce the Windows alpha setup/checksum/release record automatically from actual technical evidence, with idempotent verification and no mandatory ACCEPTANCE.json editing. Preserve genuine SDK/ABI/runtime authorization, locked assets, signed prerequisites and installer integrity; hardware acceptance remains explicitly not verified. Manual Windows build/GitHub upload needs no self-hosted runner.
- Add focused reader/geometry/storage/update/release and browser regression coverage. Document real supported formats, preparation, scientific limits and pending hardware QA.

## 0.2.2 — Geographic Mission Integration (prerelease)

- Fix the Windows checkout-repair regression fixture and documented repair to restore `assets-manifest.json` along with pinned assets. Check exact LF manifest bytes and clean fresh/repaired checkouts; retain all expected hashes and release checks.
- Handle an older maintainer checkout whose source manifest still uses CRLF when constructing the temporary Git fixture. Seed canonical LF manifest bytes, and exercise both LF and CRLF source checkouts without altering pinned upstream bytes or weakening clean-index assertions.
- Add optional user-owned Esri satellite/labels basemaps with Windows-protected key storage, explicit privacy/billing consent, shared layer preferences, attribution, authenticated provider testing and offline fallback. Offline Natural Earth remains the default; no shared quota, bulk imagery cache or mandatory account.
- Add the native toolbar Map tab with offline geography, full ordered route/pattern/area geometry, numbered/inspectable waypoints, fresh aircraft/heading, bounded recorded track, follow/free-pan/fit/zoom and cached revision/chunk synchronization over existing CommBus. Native controls/bootstrap and read-only aircraft behavior are retained.
- Add shared planned-route preview and current MSFS 2024 EFB PLN export (AppVersionMajor 12), optional airport IDs, explicit cruising altitude, unique User fixes and omitted unspecified altitudes. Add planned GPX/KML/CSV and deliberate clipboard DD/DDM/DMS/ICAO/SimBrief-format sharing; no automatic external filing or waypoint simplification.
- Preserve stable waypoint IDs, full WGS84 precision, canonical units, immutable recording snapshots, existing schema-5 data, modes, plans, presets and dual-storage settings.
- Make permanent deletion without a new backup the default for each independent native Data Management section. Improve source/count/size/scope presentation, backup labeling and irreversible-deletion warnings; retain phrases, final confirmation, cancellation, recording/transaction/lease protections and existing backups/unrelated files.
- Update installer/manual release metadata and acceptance documentation. Report all missing acceptance results/notes together. Actual authenticated imagery, Windows setup/DPAPI, MSFS/EFB/native-map and hardware performance acceptance remain pending for the changed release.

## 0.2.1 — Mission Planning & Science Experience (prerelease)

- Preserve exact bundled geography/library/license bytes on Windows Git checkouts; cover fresh checkout and existing-checkout repair without weakening SHA-256 validation. Correct the Windows guide to enter PowerShell before PowerShell commands.
- Correct existing-checkout recovery to restore both staged and working asset bytes with a per-command line-ending setting, covering the maintainer's staged CRLF changes without changing global Git settings.
- Detect actual installed/bundled SDK toolbar versions at startup; offer Update or Remind Me Later and dedicated Toolbar settings. Reject newer-package downgrades and unsupported protocols.
- Share one asynchronous graphical copy/verify progress flow across setup, startup and repair, with outside-Community staging/backups, exclusive update leases, final installed-file verification and rollback.
- Replace the coordinate grid with locally bundled Leaflet/Natural Earth geography, offline airport/city/coordinate search, pan/pinch/wheel/fit/centering, draggable route points and editable survey polygons. Optional online detail tiles require explicit disclosure and consent.
- Add eight WGS84 geodesic patterns with live preview, dimensional controls, editable ordinary waypoints, metadata, regeneration/removal and undo; no simulator writes.
- Compare recorded tracks with saved plans using ordered waypoint tolerances, gap-aware distances, route deviations, altitude errors and approximate polygon path traversal. Report methods/limitations; do not invent scientific coverage percentages or scores.
- Save reusable instrument presets independently of plans and mission snapshots; warn about unsupported future instruments rather than enabling them.
- Add synchronized Pause/Resume Science Collection while flight recording continues, retain datasets/configuration and persist deliberate pause intervals separately from unexpected cadence gaps.
- Rename Instruments to Science, preserve the old bookmark, and add a Mission Control science summary and direct link with distinct acquisition-set/measurement-value counts.
- Persist global science display units (C/F/K, inHg/hPa/mbar/kPa, kt/m/s/km/h, degrees true, kg/m³/slug/ft³, ft/m, NM/km/mi) without rewriting canonical observations or default exports.
- Redesign native Payload cards and browser Science cards, collapse diagnostics, improve all-page typography/responsiveness and introduce local Chart.js scatter/time charts with readable units/ticks/tooltips, selected ranges, expansion, point toggles, PNG and explicit display-unit CSV.
- Offer explicit permanent deletion without a new backup or recovery-backup-then-delete in native Data Management; separate destination/deletion scope and reject backups within managed data or the replaceable installation.
- Safely upgrade mission schema 4 to 5 with a verified SQLite backup; preserve telemetry, modes, datasets and source units. Keep real SimConnect ABI, read-only guidance, local archive and installer identity.
- Require matching v0.2.1 genuine SDK package/receipt and bundled geography/library/license integrity; retain the manual Windows installer/prerelease path and private-safe runtime authorization.
- Actual Windows/MSFS/native window/iPad/Quest acceptance for this version remains pending until performed against its exact installer. See `docs/v0.2.1-windows-testing.md`.


## 0.2.0 — Science Mission Operations (source; Windows acceptance pending)

- Add optional saved Mission Planner, four editable templates, ordered waypoints/survey polygons, preflight notes and immutable recording snapshots. Retain immediate spontaneous recording and aircraft-agnostic Universal/Realistic modes.
- Add real simulator atmospheric definitions and modular temperature/pressure/wind/density acquisition with independent intervals, source/units/provenance, saved/rejected/gap/cadence diagnostics and an independent scientific SQLite store. Missing readings are unavailable, never synthesized.
- Add offline WGS84 operations maps, actual/planned overlays, geographic markers/notes, instrument graphs, paged science history and reproducible research review.
- Add read-only standard AP/lateral/vertical/speed guidance and selected targets, explicit ambiguity/armed handling and deduplicated events; preserve core 360-byte telemetry/Mach ABI and native CommBus layout.
- Standardize Mach labels and requested mission wording. Remove only the toolbar scroll-region outline while retaining interactive focus/native bootstrap.
- Add trusted local independent flight/science archive clearing with previews, typed confirmations, optional verified backups, maintenance/active guards and SQLite rollback. Preserve unrelated files, settings and library identity.
- Add science-only estimates, verified schema-4 upgrades, populated-library migration coverage and complete streamed JSON/CSV/GPX/KML/text exports.
- Prepare AeroScience-Setup-v0.2.0.exe and explicit GitHub prerelease policy using the existing official-SDK/runtime/Inno/manual Windows process. No separate v0.1.2, new Windows installer/hardware pass or public v0.2.0 binary is claimed. See docs/v0.2.0-completion.md.


### Installer Pascal comment correction

- Corrected the final retention note to use Pascal comment syntax inside `[Code]`, resolving the reported line 179 `'BEGIN' expected` compiler error.
- Print the final compiler log lines and exit code automatically when installer compilation fails, while retaining the full log and required validation gates.

### Inno Setup compiler engine detection

- Check the loaded compiler engine with a no-output compile instead of rejecting Inno's valid `0.0.0.0` launcher file metadata. Retain the Inno 6.3 minimum and refuse failed or unidentified engines; record the actual engine version in release provenance.
- Discover custom/per-user installations through Windows installed-program records, show the selected compiler path/version and preflight it before the app/SDK candidate build. Keep runtime signatures, SDK validation and release acceptance unchanged.

### Isolated toolbar installation tests on simulator-equipped PCs

- Root toolbar test discovery in temporary Steam/Store profiles so an existing real Community installation does not block the build's fixture installs. Application discovery and duplicate-install protection remain enabled.
- Added a regression that detects a toolbar in a separate discovered Community folder, refuses a duplicate installation and preserves both folders. No installed add-on needs to be removed to run the release tests.

### v0.1.1 installer distribution and guided onboarding

- Made `AeroScience-Setup-v0.1.1-alpha.exe` the primary release asset using Inno Setup. Added per-user installation, branding/version/path/progress, Start and optional desktop shortcuts, Installed Apps/uninstall, launch-at-finish, a stable upgrade identity and a running-app guard.
- Bundle the self-contained application, authorized native SimConnect runtime, genuine SDK-compiled toolbar and unmodified Microsoft Visual C++ x64 prerequisite. Builds require its valid Microsoft signature/product identity and record compiler, recipe, prerequisite, EXE and payload hashes.
- Added a graphical four-step first-run wizard for independent database/library locations, Browse controls, free capacity/large-data warning and confirmed Community installation or Skip for Now. Existing paths/settings remain selected; requested changes reuse verified migrations with originals retained and conflicts refused.
- Restrict Community discovery to MSFS 2024 Steam/Store configurations and custom package roots. Retain SDK/hash/version checks, MSFS-running guards, duplicate detection and safe backups. Uninstall separately asks before moving only the recognized AeroScience package outside Community. User archives, libraries, configuration and unrelated add-ons are retained.
- Installer acceptance now binds the exact setup EXE and build receipt. Fresh installation, wizard, upgrade and uninstall preservation are mandatory alongside existing minimum live/archive checks. Finalization preserves tested installer bytes, writes its checksum/public evidence and keeps the ZIP secondary. Local Windows build/manual GitHub upload remains available without a runner.
- Updated installation/release notes, Windows/Sandbox testing and optional Actions publication. No instrument, sensor or v0.2.0 science features were added. Actual new Windows installer compilation/execution and MSFS acceptance remain pending; portable cross-builds and structural fixtures are not hardware verification.

### Recovery check output path in Windows builds

- Build the recovery test host into an explicit artifacts directory and pass its exact DLL path to the smoke check. Native Tools shells using `Platform=x64` no longer leave the check looking in the default `bin/Release` folder.
- Reject missing selected test-host DLLs before launching, and exercise the same explicit-output flow in Windows CI with `Platform=x64`. Crash/restart, sample retention, mission modes and exclusive-writer checks remain required.

### Windows release-build test cleanup

- Disabled pooling for the two temporary SQLite connections in the schema 2 upgrade test. Disposed test connections now release the archive and backup files so Windows can remove the temporary directory during cleanup.
- Retained all migration, historical Mach and mode assertions and the required release test gate. Production database connections already disable pooling; no application data or runtime behavior was changed.

### Runtime authorization confirmed; original correspondence remains private

- Recorded the maintainer's confirmation of native MSFS 2024 SimConnect.dll redistribution authorization, with no additional conditions reported. The original correspondence is retained privately and is not requested or published.
- Added `New-RuntimeApproval.ps1` to bind the public attestation to the real SDK DLL's SHA-256, public maintainer label and UTC record timestamp without manual JSON editing or copying correspondence.
- Version 2 approval records use a public authorization/conditions summary. Packaging strips private excerpts, source details, private references and extra fields; final validation rejects reintroduced private fields. Email archives are refused in distribution inputs.
- Preserved actual Windows loading/export checks, official SDK compilation/provenance and minimum real simulator acceptance. No complete release ZIP, final checksum or new Windows/MSFS pass is claimed from the authorization confirmation.

### Public alpha release preparation — local Windows build and manual upload

- Authorized public distribution through the existing repository's Releases page; removed the private destination/token requirement. A self-hosted runner is optional and is not needed for the first release.
- Added a Windows candidate builder reusing the official SDK/compiler, native runtime checker, app build and automated recovery/package checks. Full builds omit probe assets and debugging symbols; no source, test launchers or private data belongs in the final archive.
- Added candidate testing followed by finalization without rebuilding. Acceptance binds the exact clean source commit and operational payload; altered builds and stale evidence are refused. SDK hashes/ABI proof and runtime permission/loading remain mandatory.
- Added the required `AeroScience-v0.1.1-alpha-win-x64.zip` name, checksum, concise tester README/defaults and generated release notes. Version metadata supports future alpha/beta/stable releases from the shared version property.
- Defined seven minimum real Windows/MSFS alpha checks; exhaustive aircraft/Mach/storage/iPad/VR checks may remain explicitly pending for this prerelease. Stable acceptance retains extended gates.
- Added an optional two-stage public Actions flow using `github.token` with `contents: write` only in publication, plus retry-safe tag/asset checks. Conflicting existing releases/tags/assets are never overwritten; ordinary public downloads are checked after publication.
- Added official Visual C++ prerequisite guidance in Settings and documented the unresolved SimConnect redistribution designation in Microsoft's public SDK EULA. No proprietary runtime, full release archive or hardware pass is fabricated; publication remains blocked on that permission and the new gaming-PC build/tests.

## 0.1.1-alpha — Live Flight Recording Alpha (implementation; hardware acceptance pending)

- Added one Windows entry point, `AeroScience.exe`, hosting local services and opening the dashboard. Native settings manage connections, toolbar installation, storage and feedback without tester terminal commands. Offline mission history works without MSFS or internet; live recording requires fresh aircraft data.
- Removed production synthetic telemetry and demo launchers. Test fixtures remain separate; historical simulated missions retain their source labels.
- Added actual `AIRSPEED MACH` telemetry to the native definition, shared model, dashboard, toolbar, SQLite sample JSON and complete JSON exports. Historical samples may omit Mach; invalid Mach does not discard otherwise valid data. Expanded native-layout checks preserve the previous SDK corrections.
- Reproduced sample loss caused by jitter around the old interval threshold and replaced it with scheduled real-sample selection. Added configured/observed/mission-average cadence and gap diagnostics. Sustained live 1 Hz acceptance remains pending.
- Added independent Mission Database and Scientific Data Library folders with native browsing, open/default actions, usage/free-space information and the requested large-data warning. Science storage contains an identity manifest and empty organizational folders; no instruments or artificial observations were added.
- Added confirmed, exclusive migrations using SQLite backup or verified library copies, capacity/permission/conflict checks, durable recovery journals and retained originals. Active recording blocks changes. Unavailable selected stores never silently become empty replacements. Dataset IDs and library-relative references survive library moves.
- Added schema 3 with verified pre-upgrade SQLite backups, dataset references and compatibility with v0.1.0 modes/history. Export includes all recorded samples, timestamps, source labels, modes, events and dataset references.
- Preserved the native toolbar movement/resize bootstrap and single responsive Mission/Payload interface. Removed the dashboard marketing slogan and shortened user guidance.
- Added provenance/checksum gates and a manually dispatched private alpha release workflow. Public CI publishes no binaries. Official SDK output, permitted native runtime delivery and genuine acceptance evidence are required for a complete alpha archive.
- Added regression coverage for offline startup/history/export, Mach, migration/recovery, storage independence, reference integrity, recording cadence and toolbar installation. See [validation results](docs/validation-results.md) for measured results and remaining Windows/MSFS/device checks.

## 0.1.0 — development foundation (not yet a verified Community release)

### Native storage bootstrap correction — moving/resizing passed

- Actual Coherent inspection found both native movement/resize handlers absent. Manually running the installed SDK storage-ready initializer created both, restored full-window sizing, and restored moving/resizing in the user's probe test.
- Added the missing simulator-provided `/JS/dataStorage.js` import to the full toolbar and probe before native template initialization. Native handlers remain owned by the SDK; no manual initializer call, custom movement code, SDK implementation or engine change is shipped.
- Added regression coverage requiring exactly one storage bootstrap in both pages, after common.js and before the native frame import. The user subsequently confirms dragging/resizing after the `03b06f0` full-toolbar rebuild instructions. Exhaustive controls/edge coverage, startup repeatability and full mission/device acceptance remain pending.
- Added a v0.1.1 planning handoff prioritizing actual MSFS aircraft telemetry and persisted flight missions, supported by remaining acceptance tests, installable friends-only alpha assets and genuine SDK/release automation. No v0.1.1 features or automation are implemented by that report.
- Extended that planning brief with explicit v0.1.1 requirements for one user-facing executable, no required PowerShell/terminal commands, graphical first-run setup and removal of production data simulation. The current v0.1.0 app is unchanged by those future requirements.
- Recorded initial actual-aircraft telemetry success: the user's live dashboard shows connected `T-38N WSSP Late` readings and an active Universal recording with 16 samples. Completed live-history inspection and restart persistence still require testing; the v0.1.1 handoff now builds on this observed working integration.

### Native window investigation — unresolved v0.1.0 blocker

- Recorded the failed `9a1946d` desktop retest: normal title-bar dragging/resizing still fail, and dragging the resize-looking left edge moves the entire window. Latest scrolling is unconfirmed; synthetic telemetry still arrives.
- Removed Compact VR entirely: checkbox, switching logic, compact-only CSS and obsolete assertions. Mission/Payload remain in one responsive interface.
- Added a separate `-WindowProbe` SDK build with native imports/static content and no mission code or application CSS. Preserved production native configuration pending actual handler/geometry evidence; no speculative drag/resize correction is claimed.
- Corrected missing button script/template imports in the probe after actual Coherent inspection showed `buttonTemplateLoaded: false` and `headerTemplateLoaded: true`. Added a native-bootstrap parity check; corrected probe controls and native resizing require a fresh simulator retest.
- Added an opt-in passive Coherent capture for native element bounds, event paths, hit targets and errors, plus precise Windows build/replacement and desktop debugging/checklist instructions. Existing read-only CommBus diagnostics and companion are preserved.
- Replaced headset interaction acceptance with performance-only VR measurements and `vr_performance` release evidence. Diagnostic variants cannot pass the full-toolbar release gate.
- Updated browser/regression checks. Official Windows SDK builds, native input/call-stack diagnosis and hardware acceptance remain unverified in this Linux cloud. No replacement engine, scientific feature, replay or release automation was added.

### Previous toolbar interaction attempt (`9a1946d`)

- Changed registration geometry/resize hints to attributes and introduced overridable frame sizing and bounded content wrappers/scrolling. Browser tests passed but did not establish native behavior; the subsequent desktop retest failed as recorded above.
- The temporary header layout control is removed in the current revision. Earlier headset interaction acceptance was superseded by performance-only guidance.

### Windows installation-readiness review

- Added a gaming-PC workbook for preview downloads/extraction, simulated-mode checks, official SDK/runtime checks, toolbar POC/full builds, live aircraft/mission recording, VR performance, iPad LAN and GitHub backup.
- Fixed Windows separator normalization in SDK build receipts. Preserved compiler/package-tool logs and native failure codes; added explicit SDK tool/header overrides and stronger installed-header ABI checks.
- Corrected the first reported Windows SDK contract failure: verify CommBus member/payload offsets instead of treating the 32-byte fixed header as the size of the variable-length structure. Simulator acceptance remains pending.
- Accepted SDK-generated three-component minimum game versions (observed `1.9.18`) alongside four-component versions. Missing/malformed versions remain rejected, and diagnostics now include the actual value. Added packaging regression tests.
- Matched SDK layout/required-asset paths without regard to letter case while preserving original filenames and hashes for build receipts. Missing files, incorrect sizes, case-only duplicates and colliding filenames remain rejected. Added regression coverage for lowercase layouts and mixed-case/lowercase output files.
- Recorded the first user-reported successful Windows SDK proof-of-concept build through structural validation and script completion, followed by MSFS screenshots and user confirmation of diagnostic-panel registration, desktop rendering and recurring companion CommBus replies. Long-duration/lifecycle transport, mission controls, the installed folder path, full build and live telemetry/device acceptance remain pending. Added a testing handoff report for further development.
- Made the simulated launcher explicitly standalone (bridge/LAN disabled), retained launcher exit codes, and improved DLL-present/missing-dependency diagnostics.
- Added a native runtime architecture/export checker and a local feedback ZIP collector that excludes databases, SDK binaries and credential dumps.
- Prevented companion preview archives from accidentally bundling SimConnect runtimes, including case variants/nested files. No GitHub Release automation or unrelated science features added.
- All Windows/MSFS/Community/VR/physical-iPad acceptance remains pending until performed on the actual hardware.

### Initial foundation

- Added .NET 10 engine, native SimConnect telemetry provider with lifecycle/reconnect handling, and explicit synthetic telemetry mode.
- Added transactional SQLite mission recording, timestamped events, position/altitude summaries, paginated telemetry retrieval, history/detail views and recovery of interrupted missions.
- Added a responsive local dashboard with live SSE synchronization, mission controls, altitude profile and diagnostics.
- Added Universal/Realistic selection. Universal is the default; both modes persist across restarts, appear in shared web/toolbar state and history, and accept compatible aircraft without a whitelist. Schema 1 → 2 safely defaults legacy missions to Universal.
- Added graceful unknown-title and unavailable-AGL fallbacks. NASA aircraft are examples of future research presets, not required aircraft.
- Added private-LAN pairing, expiring sessions, same-origin/CSRF/Host protections, bounded logs and exclusive database ownership.
- Added SDK toolbar sources, a read-only registration/CommBus probe, mission controls and mode selection.
- Added self-contained Windows publishing, official-SDK build/ABI checks, package validation and guarded release assembly.
- Advanced Realistic Mode instrument restrictions, calibration, sensors, mapping, scientific observations and full replay remain future features. Realistic and Universal record identically in this version.
- Live Windows/MSFS, SDK package compilation/installation, Community2024, VR performance and physical iPad Safari checks remain pending. No full release archive is marked installation-ready.
