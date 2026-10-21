# Geographic Scientific Data Library

Open **Scientific Data Library → Browse & Download Data**. No flight is required.
Downloaded datasets are historical Earth observations or elevation sources, not measurements
made by your simulated aircraft. Downloading data does not add flight science yield or grades.

## Browse, review and download

1. Review scientific online access in **Settings → Scientific Data Sources**. This separate
   permission sends geographic queries and your IP address to the selected providers.
   Basemap permission alone does not enable scientific queries or downloads.
2. Choose a provider/product. For satellites, choose a bounded date range and scene-cloud
   filter, search, then select the exact scene(s). Native numeric bands and required quality
   masks are shown together; incompatible partial recipes are not offered as usable science.
3. Select an area using a small cell, multiple cells, a map rectangle, viewport, polygon,
   numeric bounding box or latitude/longitude polygon coordinates. Drawing is optional.
   US state/county boundaries can be retrieved using official FIPS codes. These are Census
   ACS2024 generalized 1:20M cartographic boundaries, not parcel/legal/navigation boundaries.
4. Choose **Preview selected download**. Review original scene dates, bands, license,
   geographic windows, verified local reuse, transfer cap and temporary disk allowance.
   Unknown subset/network/retained sizes remain **Unknown**. An allowance is a protective
   bound, not a prediction of download size or duration.
5. Choose **Approve & queue download**. Nothing downloads merely because you opened this
   page, searched, selected a scene or created a preview. Material scope/limit/source changes
   require a new preview and approval. Uncovered cells must be resolved before approval.
6. The **Download Manager** shows persisted work, actual bytes, verified items and errors.
   Pause/resume/cancel/retry operate on the companion, not just the browser. Reopening a
   completed job never restarts it. Completed numeric sources appear in retained collections
   and are eligible for compatible future missions, subject to normal source-selection rules.

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
queue as flight science and defer to active flights. Unmetered/background/consent settings
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

Advanced manual source import and existing NASA discovery/guided ENVI import remain
available. The new Library does not add NASA authorization, AVIRIS download automation or
native EMIT decoding; those are separate later workstreams. Windows/MSFS and physical
paired-device acceptance remain separate from browser and provider tests.
