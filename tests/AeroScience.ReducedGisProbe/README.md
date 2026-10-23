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

The first reduced build passed actual Windows execution in
[40009717298](https://github.com/JustSeanC/AeroScience/actions/runs/40009717298): GDAL **3.12.4**,
256-entry genuine FAA palette, exact original 8×8 numeric window, three independent CRS values with
maximum difference **0**, **0.0521 s**, **43,286,528 bytes** peak working set. Eleven native DLLs were
observed; Poppler, MySQL and the Microsoft ODBC driver are absent. This original result used dynamic
CRT and the build-tree PROJ data. It does not establish a self-contained deployed native package.

The follow-up triplet uses **static CRT** while retaining shared libraries; the deployment probe copies
only owned DLLs and `proj.db` to isolated scratch, audits direct/delay x64 imports and rejects missing
non-system dependencies or VC runtime imports. PATH/GDAL_DATA/PROJ_DATA point to that scratch, not
system Python/GDAL or the build prefix. The original first-run result must not be relabeled as the
follow-up result. Neither workflow uploads native binaries.

Pinned component notices/registry records show GDAL's MIT-style core and conditional bundled notices;
PROJ/libgeotiff/json-c/nlohmann-json MIT; TIFF libtiff terms; JPEG-turbo BSD/IJG; curl curl/ISC/BSD;
zlib Zlib; SQLite public-domain blessing; **liblzma itself 0BSD**. XZ's GPL/LGPL tool/build-script notices
do not automatically license `liblzma.dll` as GPL; those tools/scripts are not deployed. Retain the
complete collected notices with a future distribution, including IJG acknowledgement. Registry null
license fields for GDAL/XZ are not treated as an absent license. Actual native imports, codecs,
PROJ database/data licensing, vulnerability review and protected Windows package inclusion remain
gates before adding this runtime to the application graph.

The original vcpkg port's canonical SHA-256 is checked before applying the committed reduced-driver
edit. vcpkg verifies the GDAL source SHA-512 in that port and the manifest fixes its registry baseline.
The workflow records the edited recipe hash, installed package versions, every DLL's hash/size and
installed component copyright texts. Compiler/image identity remains CI-specific; this is source-pinned
research, not a claim of bit-identical native binaries across compilers.

Passing does not validate main-body chart neatlines, printed control points, every chart family,
native EMIT, installed MSFS or physical-device use. The application must retain its existing official
SDK/runtime/private-profile gates. Do not add these libraries to production until the actual Windows
result, security/component review and packaging gates pass.
