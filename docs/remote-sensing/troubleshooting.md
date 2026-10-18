# Remote-sensing troubleshooting

| Symptom | Check |
|---|---|
| Scientific data unavailable | Source import completed, correct instrument kind, files available, scene covers the actual footprint, AGL/attitude valid, no all-cloud/nodata mask |
| TIFF rejected | Classic TIFF, numeric single band, supported WGS84 CRS/scale/tiepoint, north-up, decoded block ≤32 MiB; export a bounded numeric subset from your GIS |
| ENVI rejected | Original .hdr signature/length, data dimensions/type/endian/interleave, WGS84 UTM map info, real ordered wavelengths, bbl and declared scale/unit consistency |
| Invalid bands/scene | Required ST_B10+QA or B02/B03/B04/B08+SCL, same real scene/date, matching scaling/offset; no renamed display image |
| Aircraft connected but no product | Instrument enabled and Science not paused; footprint/source coverage valid; view instrument reason/quality and retry after moving inside valid coverage |
| Import duplicates/fails | Existing catalog not overwritten; retained partial copies are counted. Keep Metadata/Work ledgers and source originals; report failure before manual recovery |
| Science storage full | Read Storage Details, adjust cap/drive or clean eligible regenerable caches; original observations are never automatic retention targets |
| Flight recording stopped | Read persistent stop reason; committed mission is preserved. Free physical space or review flight cap before starting a new mission |
| Map empty/tiles fail | Offline geography appears automatically; routes/samples stay local. Saved online preference is retained for later retry. Disable online maps entirely in Maps & Basemaps if desired |
| Historical product missing/changed | Reconnect the configured drive, preserve files and report integrity failure; do not substitute another image under the same path |
| GitHub check fails offline | Ignore nonblocking update message; local archive/acquisition continue. Disable automatic checks or retry Check for updates later |

Use **Collect Feedback** in the Windows app. Include application/source version, exact error, instrument, scene ID, supported format/CRS, source and flight dates, approximate area, relevant status and reproducible steps. Include screenshots with source provenance visible. Identify synthetic browser fixtures separately from real scientific data and MSFS captures. Do not include provider keys, pairing codes, private runtime authorization correspondence or personal datasets unless you deliberately choose to share them. A small licensed reproducible sample is more useful than a multi-gigabyte private archive. Installer logs: build output's `installer-prerequisites/inno-build.log`; local application diagnostics are accessible from Collect Feedback.

## Automatic scientific preparation

Open Scientific Data Readiness in Mission Planner, Mission Control or Science Collection. Check separate scientific consent, offline/background/unmetered policy, date window, geographic cell and cloud settings. A Ready source can still contain invalid pixels. Provider failure, missing geographic coverage, strict range rejection, cancellation, download budget and storage reservations all have explicit messages. Retry after the issue clears; do not delete the archive or raise limits blindly. 3DEP and Copernicus use different stated terrain references; no datum conversion is assumed. Hyperspectral Limited Coverage means discovery only, never a prepared spectral observation.
