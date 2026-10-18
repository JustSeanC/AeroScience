# Advanced manual scientific-source import

Normal pilots use [automatic scientific-data preparation](quick-start.md), including internal manifest generation for terrain, thermal and multispectral. This optional **advanced** guide is for users supplying their own genuine files. Distributor accounts/GIS preparation may be needed for this manual path. Both workflows publish into the same verified local Science Library; OSM/Esri remain display basemaps, never scientific inputs.

| Instrument | Supported real input | Required bands / units |
|---|---|---|
| Thermal | USGS Landsat Collection 2 Level-2 Surface Temperature, classic GeoTIFF | `ST_B10` scaled by 0.00341802 + 149 K; `QA_PIXEL`; optional `QA_RADSAT` |
| Multispectral | Copernicus Sentinel-2 Level-2A, individual classic GeoTIFF bands | B02, B03, B04, B08 in reflectance; SCL quality classes |
| Hyperspectral | A genuinely georeferenced ENVI radiance/reflectance cube, e.g. a supported AVIRIS product | ≥16 ordered original wavelength bands with units, nodata and bad-band flags |
| Terrain | One real bare-earth/elevation DEM GeoTIFF, e.g. USGS 3DEP | elevation in metres, explicit source vertical datum or explicit unknown datum |

## Format limits

- Classic TIFF, little/big endian, one channel per file: UInt8/Int8/UInt16/Int16/UInt32/Int32/Float32/Float64. Manual classic TIFF COGs are read locally; automatic provider paths prepare bounded native-grid subsets through validated remote byte ranges. Deflate/LZW strips/tiles work through managed LibTiff. Decoded blocks must be ≤32 MiB; first-directory metadata is bounded to 512 tags, 1 MiB per value array and 4 MiB total. No BigTIFF, JP2, JPEG display imagery, NetCDF/HDF, GeoPackage, LAS/LAZ or arbitrary CRS import is claimed.
- GeoTIFF must include north-up pixel-scale/tiepoint and supported CRS keys: EPSG:4326 or WGS84 UTM EPSG:32601–32660 / 32701–32760. Rotated grids require preprocessing. No silent datum conversion. Geographic pixels are degrees, not a constant metre resolution. UTM processing is limited to latitude −80…84° and points within 12° of the zone's central meridian.
- ENVI BSQ/BIL/BIP, data types 1/2/3/4/5/12/13, either endian, explicit WGS84 UTM `map info`, original wavelength units nm/um and matching header/data length. No rotated ENVI map transform. `bbl` exclusions and reflectance scale factor must match the manifest. Up to 512 channels, 1 MiB header/manifest. See [hyperspectral.md](hyperspectral.md).
- Each source file ≤16 GiB; each import ≤64 GiB; ≤500 catalogued collections. These are implementation bounds, not recommended recording sizes. Prefer a small regional subset. Paths must be relative inside the collection; traversal, absolute paths, links/junctions and alternate streams are rejected.

## Graphical preparation

1. Select your area/date in the distributor's portal. Read the product and license metadata. Landsat: [USGS product guide](https://www.usgs.gov/landsat-missions/landsat-collection-2-surface-temperature), [EarthExplorer](https://earthexplorer.usgs.gov/) (free account may be required). Sentinel: [Copernicus Data Space](https://dataspace.copernicus.eu/) (free account may be required). Hyperspectral: [NASA AVIRIS portal](https://aviris.jpl.nasa.gov/data/), [AVIRIS-NG](https://avirisng.jpl.nasa.gov/dataportal/); EMIT's NetCDF/HDF is researched but **not natively decoded**. Terrain: [USGS National Map](https://apps.nationalmap.gov/downloader/).
2. If files are JP2, BigTIFF, another CRS or excessive size, open them in a graphical GIS such as QGIS and export bounded **single-band classic GeoTIFF** subsets in WGS84 or WGS84 UTM. Retain numeric data type, nodata and original per-band grids. Do not export a colored screenshot. Use nearest-neighbor for categorical QA/SCL. Do not apply a scale twice.
3. Quality masks retain unit `1`, scale `1`, offset `0` and cannot be disabled as bad bands. Keep all bands from the **same scene and date**. Do not relabel different dates as a simultaneous image. SCL may have a coarser native grid than reflectance; no upsampling is needed—AeroScience samples each grid at the same WGS84 positions. Original GSD/sensor resolution may be coarser than pixel spacing and must stay documented.
4. Put the data and manifest in a collection folder and import through the Windows app. A failed or interrupted import is retained as unindexed processing data, not reported as complete. Existing sources/products are never replaced by reimport. Duplicate fingerprints are refused; retained failed-copy bytes remain visible under Storage Details.

## Manifest schema

All manifests use camelCase JSON. `kind` is `thermal`, `multispectral`, `hyperspectral` or `terrain`. `observationUtc` may be null when the real observation date is unknown (composite DEM); do not substitute the download date. `publicationUtc` is independently optional. Dates with known times use UTC `Z`. Required nonempty text: name/provider/license/citation/sceneId/verticalDatum. Each band can specify `sceneId`, which must match the collection.

```json
{
  "formatVersion": 1,
  "name": "My actual bounded elevation collection",
  "kind": "terrain",
  "provider": "Name of the actual data producer",
  "license": "Actual applicable license/terms",
  "citation": "Original product URL, identifier and preprocessing description",
  "sceneId": "actual-source-id",
  "observationUtc": null,
  "publicationUtc": null,
  "verticalDatum": "Source datum from metadata, or explicitly unknown",
  "notes": "Original resolution/date limitations and any crop/reprojection",
  "bands": [
    { "id": "elevation", "file": "elevation.tif", "format": "geotiff", "unit": "m", "scale": 1, "offset": 0, "noData": -9999 }
  ]
}
```

This is a **metadata template**, not a scientific dataset; replace its text and nodata with the real product information. Do not claim NAVD88 or an acquisition date unless the distributor specifies it. Original raw file hashes, copies, native grids and manifest are retained in the Science Library. No provider credential is stored in scientific manifests or exported evidence.
