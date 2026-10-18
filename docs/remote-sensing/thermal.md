# Thermal Imager

Normal pilot setup: [automatic quick start](quick-start.md) and [provider capabilities](automatic-data.md). This guide retains optional advanced manual-file instructions; normal users do not need to prepare these files themselves.

Import **Landsat Collection 2 Level-2 Surface Temperature** `ST_B10` and `QA_PIXEL` from the same real scene. ST_B10 DN uses the official conversion `K = DN × 0.00341802 + 149`; a scene without Level-2 ST is unavailable. Use the source nodata value (normally fill DN 0). Optional QA_RADSAT masks saturation. Do not substitute SR_B10, display brightness or aircraft ambient temperature.

In a `kind: "thermal"` manifest use these bands, plus the real provider/citation/license/date/scene metadata described in [sources.md](sources.md):

```json
"bands": [
  { "id": "ST_B10", "file": "actual-scene_ST_B10.TIF", "format": "geotiff", "unit": "K", "scale": 0.00341802, "offset": 149, "noData": 0 },
  { "id": "QA_PIXEL", "file": "actual-scene_QA_PIXEL.TIF", "format": "geotiff", "unit": "1" }
]
```

The acquisition uses nearest original pixels inside an approximate attitude-aware footprint. Fill/dilated cloud/cirrus/cloud/shadow/snow (QA bits 0…5), saturation if supplied, nodata and implausible 100…500 K values are masked. All invalid means no product; partially valid products retain nulls and counts. More detailed uncertainty/emissivity/atmospheric correction is not implemented. Landsat's delivered pixel spacing is not an aircraft camera's resolution or proof of independent thermal resolution.

**View Data** shows real temperature samples, color legend, unit label, extrema/valid count and geographic sample positions. Temperature follows saved science display preferences; canonical JSON and default CSV retain Kelvin source values. Transparent cells are missing, not zero temperature. PNG is a color preview, not a temperature GeoTIFF. Read original observation, flight-packet and processing dates separately. A flight today over a 2020 scene is labeled historical source sampling, not a live aircraft thermal image.

First workflow: obtain a small, clear Level-2 ST subset, import it, check coverage, enable Thermal Imager, fly a short level pass, pause/resume science and inspect/export the saved product after closing MSFS. No modeled thermal mode is provided.
