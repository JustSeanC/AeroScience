# External Data Manager — historical scientific datasets

Open **External Data Manager → Find & Download Data**. No flight is required.
Downloaded datasets are historical Earth observations or elevation sources, not measurements
made by your simulated aircraft. Downloading data does not add flight science yield or grades.

## Browse, review and download

1. Review scientific online access in **Settings → Scientific Data Sources**. This separate
   permission sends geographic queries and your IP address to the selected providers.
   Basemap permission alone does not enable scientific queries or downloads.
2. Choose **What scientific data do you need?**: terrain/elevation, thermal, surface
   elevation, multispectral, hyperspectral or EMIT development validation. The page selects a compatible
   provider and explains what the measurements represent. Terrain is a numeric elevation
   grid; it is not a downloadable aircraft LiDAR point cloud. Provider details remain advanced.
3. Find a place/airport, select a small cell, draw a rectangle, use the visible map area, or
   choose a **named state/county** from authoritative Census lists. Multiple cells, polygons,
   numeric bounds and FIPS identifiers remain under Advanced area selection. Census ACS2024
   1:20M generalized boundaries are cartographic, not legal/parcel/navigation boundaries.
4. Choose **Find Available Data**. Satellite results request provider-side latest/oldest/cloud
   ordering with a stable ID tie-break, and retain original observation dates. A recommendation
   is made only when the returned provider order can be confirmed. It is not a claim of the
   newest valid pixels worldwide. Composites have no invented observation date. An empty result
   can be followed by an explicit **Search earlier years**; outages are shown separately.
   NASA defaults span its historical archive. Manually edited dates are preserved.
5. Inspect the ordinary science/date/quality summary, select the exact dataset(s), and choose
   **Review & Download**. Exact native numeric bands, scale/offset, CRS, masks, IDs and revisions
   remain available in Technical details. No alternate scene is silently substituted.
6. Review reuse, scope, actual known file sizes, Unknown subset sizes, physical drive space,
   the flight-recording quota (separate from Library capacity) and the independent transfer cap. **Download & Import** explicitly
   approves that work. Searching, opening the page or creating a preview does not approve it.
   Changed scope, source revisions or budgets require a fresh preview and approval.
7. The Download Manager shows durable state, measured network bytes, original file progress
   where its denominator is known, verification/import and final Dataset ready. Pause/resume,
   cancel and retry operate on real backend jobs. Reopening a completed job never restarts it.
   Verified retained sources are reusable by compatible missions; they do not add flight yield.

## Preparation scope and in-flight retrieval

Mission Planner's **Prepare scientific data** opens a review with three choices:

- **Entire Mission**: the complete bounded plan inventory.
- **First Half of Mission**: the first 50% by geodesic length of an unambiguously ordered
  collection route. Explicit waypoint collection actions or route portions within declared
  survey polygons establish this order; transit is excluded where known. Polygon membership
  is bounded at 350 m along the route. Source preparation cells extend beyond the route itself.
  Ambiguous/repeated/area-or-gate-trigger sequences and boundary pause/resume policies cannot
  assume continuous collection intervals; use Entire or Choose Areas for those plans.
- **Choose Research Areas**: named planned survey areas and individual ordered route segments,
  with a map preview and accessible checkboxes. Unsupported/empty selections are rejected.

The approved scope is stored against the saved plan revision. Scope changes preserve verified
sources and reconcile pending work. Expanding to Entire reuses retained exact content. The full
plan's readiness denominator and frozen grading obligations do not shrink with preflight scope.
The review reports selected units separately, and local envelopes are reuse candidates until
verified; provider/QA availability can still prevent usable observations.

During an active flight the matching plan can prepare upcoming missing regions beyond its
preflight scope, subject to explicit scientific consent, background/network policy, in-flight
allowances, storage and flight priority. Current aircraft/predicted arrivals and plan-route
order reprioritize the existing deduplicated queue. In-flight preparation uses at most one
shared transfer; science-processing backlog, stale telemetry or host-memory pressure can
temporarily hold it and resume it during the flight. Separate user-directed Library downloads
yield to active flight work. Paused/cancelled plan preparation does not silently restart.
After End Flight, unfinished plan work waits for **Continue preparation for a future flight**.
Late source arrival never backfills a missed live scientific observation.

When you select **Start Mission**, incomplete external preparation shows **Some Scientific
Data Is Not Ready**. The advisory separates the full mission inventory from First Half or
Custom Areas and labels unassessed/stale/unavailable counts honestly. **Start Mission Anyway**
uses the normal recording checks without changing preparation or source choices. **View Data
Preparation** opens the existing activity page; **Continue Setup**, X and Escape return without
recording. Fully ready plans need no extra confirmation. Starting a flight or science session
does not guarantee valid measurements everywhere; upcoming retrieval remains conditional.

## Progress and time estimates

Download Activity displays **each plan's complete instrument × region inventory**, including in
All Activity. Its recent-job list and global queued/active counts describe a bounded processing
window, not the number of mission regions remaining. Separate plans are never averaged into
one readiness percentage. For example, a 204-region/two-instrument plan retains 204 logical
regions on each instrument card even when only 24 jobs are currently queued/active.

Known original lengths allow a transfer progress bar. COG subsets whose required native blocks
are not yet known show Unknown instead of using the approval cap as a denominator. Network
bytes (including retries), resumed original bytes, verified source reuse and Library publication
are separate counts. A file reaching 100% is not a claim of scientific QA validity or import.

After sustained measured transfer, an EWMA-smoothed recent speed supports an approximate
remaining network-time range. The estimate disappears when stale, paused, queued or processing.
Comparable same-provider transfers within this process can inform a subsequent preview;
otherwise **Time estimate available after download starts**. Verification/import time stays
Unknown. No speed-test traffic, assumed connection speed or reliable whole-queue finish time
is promised. Timing history is bounded and contains no credentials or signed URLs.

## Manage downloaded data safely

Collections group retained source regions without merging scientific originals. Inspect source
observation dates, units, provider, managed bytes, coverage and source-selection state.
**Exclude from Active Science** retains files and is reversible; deletion actually removes
reviewed managed copies from the current Library.

Choose one source, selected regions, a collection or **Delete All Downloaded External Data**.
One accessible review shows the exact selection, region count, estimated managed bytes and any
blockers. Use **Cancel** or the red **Delete Data** / **Delete All Downloaded Data** button.
There is no typed phrase or browser-native confirmation. X and Escape dismiss an unapproved review;
closing an already approved operation does not cancel it. Reviews expire after ten minutes and
files/ownership/dependencies are rechecked. A blocked batch is never silently reduced.

External-source deletion preserves independently published mission measurements, source snapshots,
units, dates, provenance and grades supported by retained evidence. Saved-sample maps, spectra,
comparisons and composites do not need the original raster. New source-pixel sampling and retained-
source mosaics require originals again. Modeled geometry is not verified scientific coverage.
Active flight/acquisition/analysis and runnable preparation still block deletion; pause/cancel
preparation first. Explicitly requested preparation can subsequently fetch missing approved data.

Deletion creates no recovery archive. Approved originals, attributable staging/range copies and
retained-source derived copies are purged; selected content in registered managed database/recovery
backups is also purged without removing unrelated records. Saved mission-sample products are
independent results, not original-source caches. Unknown ownership, changed files, a shared mosaic
with unselected contributors or an unavailable retained migration copy blocks complete deletion.
Older unregistered copies outside known managed locations require explicit reconciliation; AeroScience
cannot discover arbitrary offline/user-made archives or claim to erase operating-system backups.

An atomic database receipt precedes irreversible file removal. A precommit failure changes no files;
a committed interruption resumes exact cleanup on restart. Locked files or unfinished database/WAL
cleanup remain **Cleanup pending**, never Completed. Logical bytes and physical free-space change
are distinct. SQLite selected rows use secure deletion/checkpoint/compaction; this is managed-data
removal, not a forensic SSD or system-backup erasure claim.

**Delete Collected Science** in Mission History (or the Windows Data Management scientific clear)
is a separate one-review action. It purges selected observations, products, thumbnails, generated
analysis, auxiliary scientific evidence, instrument measurement summaries, grades and registered
recovery copies. Deleted evidence cannot automatically regenerate an old grade or reconstruction.
Raw flight recordings/telemetry, plans, instrument configuration history and external source datasets
remain separate. Shared historical journals or unavailable copies require explicit resolution.

## Storage location and Library scale

**Open Data Folder** and **Copy Path** appear in both Manage Downloaded Data and Find & Download
Data, with configured path, managed usage and physical free space (or Unknown). A trusted local
Windows companion opens Explorer; paired iPads/browsers only copy the path. Each retained source
also offers **Open Source Folder**. Identity/path validation is repeated; missing drives never cause
an empty replacement directory.

There is no overall source/collection/region count ceiling or default scientific-retention quota.
Physical capacity and write/recovery headroom govern retained size. Flight-recording quotas,
per-job transfer allowances, bounded work units, request pages and concurrency remain independent.
Collections and regions use full-inventory counts and bounded pages; the geographic source index
searches the complete catalog. A page limit is not a Library capacity limit. Existing exact verified
sources are checked before transfer. Failed 3DEP-style imports can re-enter the existing validator
from exact, owned completed staging after restart without transferring the dataset again; mismatched
or invalid staging is not accepted as verified. Compatible multi-source gap-only preparation is not implemented in this checkpoint; it remains a required follow-up.

## Products and scientific meaning

| Provider | Numeric product | Important limits |
|---|---|---|
| USGS 3DEP | Bounded F32 bare-earth elevation composite | Regional US coverage; actual nodata is checked. Composite survey dates, native resolution and vertical datum are unspecified. No dated scene catalog is invented. |
| Copernicus / Planetary Computer | GLO-30 DSM | Surface elevation includes buildings/vegetation; EGM2008 reference. Publication date is not a per-pixel observation time. |
| Landsat / Planetary Computer | Collection 2 Level-2 L2SP ST_B10, QA_PIXEL and QA_RADSAT | Source temperature scaling and masks retained. 30 m delivered grid; 100 m native TIRS. Historical surface temperature, not an airborne thermal camera. |
| Sentinel-2 / Element 84 Earth Search | L2A B02/B03/B04/B08 and SCL | Product-specific reflectance scale/offset retained; 10 m reflectance and native 20 m SCL. Scene cloud percentage does not prove valid pixels in your area. |

Provider outlines, retained numeric footprints and actual QA-valid acquired coverage are
separate concepts. Neither a successful download nor a colored map outline promises valid
pixels throughout a region. Source dates, georeferencing, units, quality masks, limitations
and provenance remain available in the retained source metadata.

## Bounds, storage and recovery

Jobs support up to **4,096 work units**, subject to existing provider/geometry limits.
A work unit is a small geographic numeric window, not an individual satellite pixel.
Large regions or many scenes may exceed this limit. **Divide into bounded regions** proposes
explicit smaller selections; it does not approve or queue them automatically. Narrow the
scene/date range where appropriate. Dateline-crossing, polar, extreme-extent or unsupported
geometries must be split rather than interpreted as worldwide downloads.

Existing 25 GB mission and 50 GB Scientific Library defaults, disk-space reservations and
pinned/source protections remain in effect. Library jobs use the same bounded preparation
queue as flight science and yield to active flight acquisition. Unmetered/background/consent settings
are honored. Provider 429/503 responses or outages may require an explicit retry; limits are
never silently enlarged. Actual received bytes remain charged across retries. A process kill
can leave an unresolved read reservation, shown separately as an interrupted-read allowance.
This conservatively protects the approved cap without calling unknown bytes measured bytes.

Safe COG partial reuse requires a fresh exact HTTP range response, matching strong ETag and
length, and matching saved-block hashes. Unsupported resumption restarts bounded reads;
changed approved identities require a new preview. Partial files never appear as ready
sources. Cancelling a job preserves verified originals and protected data; interrupted owned
staging remains accounted for recovery, not silently treated as usable data or deleted.

Older manual/automatic sources without a verifiable catalog revision stay usable through
the existing mission source selector. Exact Library-job reuse requires matching retained
revision provenance; missing historical identity is never invented.

## Offline operation

Retained collections, metadata, existing jobs and compatible mission reuse remain available
without Internet access. Choose **Use cached catalog / boundaries only** to inspect previous
searches with their original as-of time. Cached results are not a new live availability check.
Exact verified local content can be reused without scientific network consent. An unavailable
external Library drive produces an error; an empty replacement database is never created.

## NASA hyperspectral assistant

Choose **Hyperspectral Research** under What scientific data do you need? on Find & Download Data. Search actual
NASA CMR flightlines by product, original date, bounds and optional scene identifier.
Footprints are catalog outlines, not guaranteed valid pixels. For compatible Classic/NG
original ENVI reflectance, choose **Connect Earthdata on this PC** once, sign in on NASA's
official site and save its User Token in the masked Windows dialog. Then select a scene's
**Preview NASA Download & Import**, review the exact original files/bytes and click
**Download & Import**. Existing Library jobs handle progress, pause, resume, retry,
restart recovery, verification and automatic atomic import without a flight.

Complete original cubes can be large: geographic selection discovers flightlines; this
workflow does not claim NASA subset downloads. The separate NASA scene cap defaults to
1 GiB; review it and the preflight allowance in Scientific access settings when necessary.
The automatic subset cap is unchanged. Exact verified retained sources can be reused
offline without a token or another transfer. Tokens expire; reconnect when NASA denies
access. Connection/disconnection takes place on the PC; paired clients can use an already
connected Library. Neither token nor password is sent to the browser dashboard.

**Open official NASA access / Earthdata** and **Verify downloaded AVIRIS files on this PC**
remain the browser/manual fallback for unsupported access or formats. Those browser
transfers have external progress. No JSON editing or terminal is required in either workflow.
EMIT is **discovery / official access only**, with no native NetCDF-4 import.
Read the [supported formats, steps and acceptance limits](remote-sensing/hyperspectral.md).

**Import Files Already on My Computer** remains an advanced manual alternative. Closing its
window does not cancel an ongoing import. Use the explicitly labeled Cancel import control
for that operation. Applicable popups have a touch-sized top-right X, Escape dismissal and
focus return; dirty popup fields offer Keep editing before discarding unsaved edits. Windows/MSFS and physical
paired-device acceptance remain separate from browser and provider tests.

## EMIT historical swath exploration (unreleased 0.6.0)

**EMIT · development validation** connects exact RFL002 / uncertainty002 / compatible MASK003
originals to the existing Library jobs and trusted local-file picker. Explicit approval and a
pending-genuine-acceptance acknowledgement are required. Retained sources provide spectrum and
uncertainty charts, QA-aware bounded GLT map windows, same-scene comparison and portable JSON.
This adapter does not enable automatic aircraft sampling or create flight observations/grade credit.
Genuine NASA originals, authorized direct-access redirects and installed-device acceptance remain
pending. Follow [the current workflow and limitations](research/v0.6.0-emit-integration.md).

### v0.6.0 development: cumulative retained reuse

Download previews and preparation now search the indexed retained inventory before transfers.
Several compatible verified request windows can satisfy one planned region. Native numeric reads,
nodata and the existing thermal/Sentinel quality rules establish support; outlines alone do not.
Source dates, units, scaling, datum, recipe and processing identity remain distinct. Each observation
still identifies its actual original source. No common-resolution mosaic or past airborne observation
is created by preparation.

For supported numeric subset providers, reviewed work separates reused windows from actual gaps.
Only gaps are requested; optical regional expansion is disabled when it would redownload retained
pieces. Exact simultaneous preparations share one verified original. Original bytes are counted once
in the preview; this retained size is not an estimate of avoided network traffic. NASA full-original
jobs retain their existing exact-scene/companion reuse and explicit full-file sizes.

Union validation is bounded and cancellable. Unaligned grids, incomplete projected QA, unavailable
originals or missing historical request/version contracts remain Unknown/limited, rather than a false
positive coverage claim. Exact retained reuse remains available; narrow the review or inspect the
source where a partial union cannot be verified. These are individual work/read bounds, not a
maximum Library collection count. Preflight review queries every selected region through the index;
uncertain assessments are separate from reusable counts. Physical storage and existing quotas,
source/network consent, pause/cancel, flight priority and permanent deletion remain enforced.

### Inspect retained numeric values

Open a retained region's details and choose **Inspect Numeric Values / Map**. Read a small window
in the original dataset, view its scalar legend and native cell locations, inspect the paged numeric
table, or export CSV with units, source date, native row/column, declared scaling, quality and original
file hashes. QA-excluded values remain distinct from decoded source values; empty/invalid pixels do
not receive valid colors. NDVI is explicitly derived. Native resolution is not improved by the display.

This is historical source exploration, not a recorded aircraft measurement or acquisition credit.
The chart's sample index is not flight distance. Large-source inspection yields while flight recording
is active. Closing or changing the window cancels the read and clears stale results; originals remain
unchanged. EMIT uses its existing experimental Spectrum / Map / Export explorer and separate
original-data acceptance gate. Installed/device and genuine-original acceptance remain separate.
