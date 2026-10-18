# Scientific exports and portable evidence

Open a saved product in Science Collection or Mission History; export links use the local authenticated service and do not upload research.

| Format | Meaning |
|---|---|
| JSON | Versioned product, samples, source grids/files/hashes/provenance, null quality gaps, footprint and evidence context |
| source.json | Original collection manifest, managed file hashes and native grids, without copying raw source files |
| CSV | One row per sample/band; WGS84 coordinates, original band/wavelength/value/unit and source/flight/processing dates |
| GeoJSON | WGS84 sample features and approximate footprint; numeric values and source metadata |
| GPX / KML | Geographic footprint for reference; no invented altitude/laser measurements |
| PNG | Rendered color preview of supported scalar/RGB display, with no claim to be numeric GeoTIFF or full scientific raster |
| evidence.json | Lightweight local evidence with flight conditions/aircraft/Mach, science mode, acquisition settings/mount, plan snapshot, provenance/product IDs/hashes and a sample spectrum |

Canonical values remain in source units: thermal Kelvin, reflectance/radiance as labeled, DEM metres, NDVI unitless. UI temperature conversion does not rewrite observations. A browser PNG's view/stretch/units differ from canonical data; save JSON/CSV and source metadata for quantitative use. A 24×24 sampled product does not improve original pixel resolution and may contain repeated source pixels. Source scenes/observation dates must be considered when comparing products. GPX/KML exports are not a point-cloud substitute. GeoTIFF/LAS/LAZ writers are not implemented.

Mission reports append actual remote product references, sample counts, dates, hashes and limitations without generating scientific conclusions. Compact evidence is designed for future organizational review, not implemented campaign submission. It remains local by default, carries no authentication secrets, and may not be sufficient to independently reproduce a multi-band scene without its referenced source/product. Hashes detect changed bytes; they are **not tamper-proof proof that a simulated flight occurred**, and no local objective pass/fail is represented as authoritative.
