# Scientific flight quick start — v0.3.2

1. Launch AeroScience from Start. Under **Settings → Scientific Data Sources**, choose **Enable Online Scientific Data Access** and review its separate privacy disclosure. Imported datasets can remain offline-only; map consent is independent.
2. Create/save a mission and enable the required existing instruments. Saving alone does not download. Choose **Prepare scientific data**, review the route/survey, local coverage candidates, free space and preflight limit, then choose **Download Scientific Data**. A size may remain unknown until a provider/source is selected.
3. Use **View Download Activity** for progress, actual bytes, active/queued/failed work and Pause/Resume/Cancel. Preflight volume is separate from the in-flight rolling-hour allowance. Both are internet data usage. Unlimited is optional and does not remove other protections.
4. In MSFS open AeroScience → **Mission**, select the saved plan or Unplanned Mission and **Start Recording**. Use the moving map or a compatible exported route to navigate. AeroScience does not command your aircraft/autopilot. Scientific preparation follows actual motion, including off-route or route-free flight; unfinished speculative preflight pauses.
5. Read Preparing data, Ready, Outside coverage, No valid pixels, Processing delayed or Error accurately. A ready source can contain masked pixels. Missing science does not stop valid flight recording and is never backfilled.
6. End the mission. Open **Mission History → Scientific Results → Instrument** to review acquisitions, atmospheric values, actual locations and exports. Original satellite/elevation observation dates are separate from simulated flight and processing times.

Historical original sources and completed products are retained in your configured Scientific Data Library. Use grouped **View Collection Coverage** and **View Individual Regions** to inspect their real extents and provenance. Repeated prepared areas can reuse these originals across missions; there is no guarantee of worldwide/cloud-free coverage.

Terrain, thermal and multispectral use supported genuine public numeric datasets. For NASA hyperspectral data, use **Library → Browse & Download Data → Hyperspectral** and the [NASA assistant](hyperspectral.md): discover actual scenes, follow official browser access, then verify/import compatible original ENVI files. [Advanced local import](using-your-own-hyperspectral-dataset.md) remains available. Browser acquisition is not automatic app downloading; EMIT is discovery-only. No QGIS, Python, terminal or hand-written manifest is required for ordinary pilot operation.

Provider/network/storage limits and masks can prevent preparation. Choose offline operation with retained valid data, change the area/date/quality policy or use the optional advanced import. Do not interpret a status or map outline as a fabricated measurement or continuous image. Calibrated sensor overlap and stitched mission imagery are future work.

For detailed formats and limits see [Scientific Data Sources](scientific-data-sources.md), [automatic preparation](automatic-data.md).
