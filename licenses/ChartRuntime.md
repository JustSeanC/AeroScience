# Optional v0.6.0 source-built chart runtime — notice supplement

This additive supplement preserves the historical third-party notice file unchanged.
The optional private chart runtime has a complete file/data manifest and full component notices
under `ChartRuntime/licenses` in applicable private packages. It is not in the published v0.5.0 installer.

| Component | Version | Distribution terms / scope |
|---|---|---|
| GDAL | 3.12.4 | MIT-style and bundled component notices; reduced drivers only |
| PROJ | 9.8.1 | MIT; source/data notices and unchanged projection database retained |
| libgeotiff | 1.7.4 | Upstream permissive notices |
| TIFF | 4.7.2 | BSD-style notices |
| zlib | 1.3.2 | zlib license |
| liblzma | 5.8.3 | 0BSD library; GPL utilities are not shipped |
| curl | 8.22.0 | curl license; no new application network-fetch interface |
| JSON-C | 0.19-20260627 | MIT |
| SQLite | 3.53.4 | Public domain; separate PROJ data, not a replacement mission/science store |
| libjpeg-turbo | 3.2.0 | IJG/BSD-style; static libjpeg API, TurboJPEG disabled |
| nlohmann JSON | 3.12.0 | MIT, header-only; full notice required despite no separate DLL |

The eleven complete notices are copied from the exact source-built ports and included/hash-verified
even for static/header-only dependencies. This summary does not replace them. Host build tools,
proprietary/GPL optional GDAL dependencies and the rejected generic GIS bundle are excluded.
Public distribution approval remains separate from private source/compile/loading checks.
See the [aviation guide](../docs/v0.6.0-aviation-reference.md#chart-runtime-provenance-and-security)
for the JPEG advisory, restricted read-only API and remaining acceptance. No blanket upstream
vulnerability-free or owner-installed acceptance is claimed.
