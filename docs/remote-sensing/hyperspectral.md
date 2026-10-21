# Hyperspectral Imager

## NASA catalog browsing (0.5.0 development)

Open **Scientific Data Library → Browse & Download Data → Data family → Hyperspectral**.
Choose AVIRIS-Classic, AVIRIS-NG or EMIT, select coordinates/viewport/rectangle/polygon or
an existing regional boundary, adjust dates and optional flightline filter, then search.
NASA CMR returns bounded pages of actual granules, source dates and original footprints.
The map distinguishes these remote outlines from verified retained local data. Neither
outline guarantees valid pixels. Offline cached searches retain their original as-of time.

Classic Collection `AVIRIS-Classic_L2_Reflectance_2154` V1 and NG
`AVIRIS-NG_L2_Reflectance_2110` V1 use family-specific original ENVI companions.
`EMITL2ARFL` V001 is **discovery / official access only**: native NetCDF-4 import is
unsupported. Missing spectral/grid metadata stays Unknown until original-file inspection.
Choose **Open official NASA access / Earthdata**. NASA handles account login, permissions,
terms and original-file download in the external browser. AeroScience does not request
passwords, register an undocumented OAuth client, collect browser cookies or store tokens.
Protected original Classic and NG header requests returned HTTP 401 without individual
Earthdata authorization in development. No direct authenticated app-download claim is made.

After downloading, keep the matching reflectance `.hdr` and binary together and choose
**Verify downloaded AVIRIS files on this PC**. The exact granule/collection revision is
rechecked before opening the existing import wizard. Canceling NASA login/download or the
file picker leaves the Library unchanged. Removed/revised scenes require a new search.
Paired browsers can discover and open official access; trusted local-file selection remains
on the companion PC. Browser transfers are not managed Library jobs, so their budgets,
pause/resume and progress are controlled by the browser/provider. Check disk space before
downloading; AeroScience enforces Library limits again at import. No flight is created.
The catalog revalidates the official collection version and never substitutes another product.

[Live catalog evidence](../research/v0.5.0-nasa-validation.json) distinguishes metadata
acceptance from family-specific original-file numerical verification and installed testing.

## Advanced local import reference

Normal pilot setup: [automatic quick start](quick-start.md) and [provider capabilities](automatic-data.md). Automatic NASA catalog discovery is supported; automated hyperspectral file preparation/authorization is not. The following ENVI workflow remains advanced.

The implemented minimum workflow reads **real georeferenced ENVI cubes** with ≥16 original wavelengths, not a generated spectrum or static graph. Candidate producers include NASA AVIRIS and AVIRIS-NG. Their products vary in format/projection and availability; not every portal item is directly supported. EMIT is a researched source, but its native NetCDF/HDF is not decoded here. No Earthdata token is embedded in the app.

Obtain a licensed radiance or reflectance cube, its original `.hdr`, wavelength list, bad-band mask and geographic metadata. Use WGS84 UTM north-up map info. Unsupported projections/rotation must be explicitly prepared in a scientific GIS; unlocated raw flight-line data cannot simply be assigned coordinates. Keep all real bands together. Wavelength units must be nm or micrometres; the reader converts wavelengths to nm and preserves measured values.

Create a `kind: "hyperspectral"` manifest with one band entry per cube channel, channel index starting at 0. For example (two entries shown—**the real manifest must contain at least 16 ordered bands**):

```json
"bands": [
  { "id": "band-001", "file": "actual-cube.img", "headerFile": "actual-cube.hdr", "format": "envi", "channel": 0, "unit": "reflectance", "scale": 1, "offset": 0, "noData": -9999 },
  { "id": "band-002", "file": "actual-cube.img", "headerFile": "actual-cube.hdr", "format": "envi", "channel": 1, "unit": "reflectance", "scale": 1, "offset": 0, "noData": -9999 }
]
```

Actual wavelengths are read from the header. If supplied in the manifest they must match. `badBand: true` must honor every zero in `bbl`; missing bands remain graph gaps. Use the real reflectance scale factor (if present in the header it is checked). Supported homogeneous units are `reflectance`, `radiance W/(m2 sr um)`, `radiance W/(m2 sr nm)`, `radiance mW/(cm2 sr um)` or `radiance uW/(cm2 sr nm)`. Do not relabel radiance as reflectance; automatic atmospheric correction/unit interconversion is not provided.

Enable Hyperspectral Imager over that collection, record and open **View Data → spectrum**. Select a geographic sample index; the viewer shows its coordinates and a chart of **original wavelength versus measured/scaled value**, with units. Canonical JSON and spectral CSV retain all sampled channels/quality gaps, source date and native grid. This does not identify minerals/species or perform spectral-library classification/calibration. The storage estimate grows with actual imported band count; 24×24 samples are bounded extractions, not a new full-resolution flight-line cube.
