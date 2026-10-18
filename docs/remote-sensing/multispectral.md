# Multispectral Imager

Use genuine Sentinel-2 **Level-2A** surface reflectance bands B02 (blue), B03 (green), B04 (red), B08 (near infrared), plus SCL. JP2 distributor files must be exported to numeric single-band classic GeoTIFF before import; this release does not decode JP2. Preserve original native grids, including SCL's coarser spacing.

Set `kind: "multispectral"`, each reflectance band's `unit: "reflectance"`, the actual product scale and additive offset, and `SCL` unit `1`, scale 1/offset 0. **Do not universally assume DN/10000**: processing baselines can contain radiometric offsets; read the source metadata and any transformations already applied by the distributor. Use nodata from the actual file. Record preparation/scaling in citation/notes. B02/B03/B04/B08/SCL must be one scene/date. See [sources.md](sources.md) for the collection manifest.

The viewer offers:

- **natural color:** R=B04, G=B03, B=B02;
- **false color:** R=B08, G=B04, B=B03;
- **NDVI:** `(B08 − B04) / (B08 + B04)`, unitless, invalid denominator and results outside −1…1 left missing.

RGB previews use an explicit fixed 0…0.3 reflectance display stretch; clipping the display does not change stored band values. SCL excludes classes 0,1,3,8,9,10,11 (nodata, defective, shadow, cloud/cirrus, snow/ice); remaining classes are not a guarantee of scientific validity. Every raster is sampled in its own grid at the same WGS84 positions, without claiming finer source resolution. All-cloud acquisitions report unavailable.

NDVI is a vegetation index, not a direct biomass/health measurement or live foliage sensor. Acquisition date, atmospheric correction and source quality affect interpretation. The exports preserve underlying bands, original grids and nulls so a future reviewer can recompute the ratio.
