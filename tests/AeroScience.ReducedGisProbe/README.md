# Reduced chart codec research

This project is excluded from the solution and production dependency graph. It builds **GDAL 3.12.4**
from the source/hash-pinned vcpkg port at `1391150599c0b9dc6f2a3c79c50eff6c94b077d1`, with default
features and optional drivers disabled, explicitly enabling GTiff/VRT. A direct, small C-ABI probe
avoids coupling a 3.13 managed wrapper to a different native version. No user Python installation is
required. Python in this research workflow patches a verified build recipe and acquires one bounded
genuine FAA fixture; it is developer tooling only.

The Windows workflow builds in runner scratch, self-contained publishes the locked .NET probe,
executes the actual DLL, compares original palette indices and three CRS transformations against
independent rasterio values, caps GDAL's cache, and records memory/time. It uploads **receipts and
copyrights only**; native binaries and originals are never uploaded or distributed.

GPL components are not automatically undistributable. A combined GPL distribution can require
license compatibility, notices and complete corresponding source/build materials. The earlier bundle
also includes a separately licensed Microsoft driver and lacks a complete corresponding component
inventory. This smaller chart-only build avoids unnecessary PDF/database dependencies, rather than
assuming the MIT NuGet wrapper label covers all runtime files. Its expected dependencies are GDAL,
PROJ, TIFF/GeoTIFF, JSON-C, zlib and their pinned transitive dependencies. **Actual** component versions,
copyrights, native imports and redistribution obligations still require review from the build receipts.
No source/notice obligation is waived and no production-distribution approval is inferred.

The original vcpkg port's canonical SHA-256 is checked before applying the committed reduced-driver
edit. vcpkg verifies the GDAL source SHA-512 in that port and the manifest fixes its registry baseline.
The workflow records the edited recipe hash, installed package versions, every DLL's hash/size and
installed component copyright texts. Compiler/image identity remains CI-specific; this is source-pinned
research, not a claim of bit-identical native binaries across compilers.

Passing does not validate main-body chart neatlines, printed control points, every chart family,
native EMIT, installed MSFS or physical-device use. The application must retain its existing official
SDK/runtime/private-profile gates. Do not add these libraries to production until the actual Windows
result, security/component review and packaging gates pass.
