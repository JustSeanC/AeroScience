# Hyperspectral Imager

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
