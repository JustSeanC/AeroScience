# Native GIS research probe

Opt-in development experiment, **excluded from AeroScience.sln and every application dependency graph**.
It does not authorize redistribution or add a product feature. Python only acquires the research fixture;
installed pilots do not need Python. No native binaries or original chart are checked in/uploaded.

Pinned MaxRev.Gdal Core / WindowsRuntime.Minimal / LinuxRuntime.Minimal.x64 **3.13.3.583** use
locked NuGet content hashes. The manual `native-gis-prototype` workflow restores locked, publishes
self-contained, executes the actual native process on both platforms and verifies locks unchanged.
It checks compressed GeoTIFF and NetCDF4/HDF fill/windows, native disposal and a genuine FAA TIFF's
hash, palette values and three CRS transforms against independently recorded rasterio 1.4.3 / PROJ
values. Synthetic NetCDF is **not EMIT**; transformed pixels are **not printed control points/neatlines**.

To reuse the already retained genuine original locally:

```text
dotnet restore tests/AeroScience.GisProbe --locked-mode
dotnet publish tests/AeroScience.GisProbe -c Release -r linux-x64 --self-contained true --no-restore -o <new-research-publish-folder>
<new-research-publish-folder>/AeroScience.GisProbe <retained-Hawaiian-Islands-SEC.tif> <research-result.json>
```

Use `win-x64` and `AeroScience.GisProbe.exe` on Windows. The explicit `RuntimeIdentifiers` are part of
the lock graph. A publish must not alter dependency locks. The host-created temporary raster directory
is owned by the probe and removed after native handles are disposed. The genuine source is read-only.
The manual workflow obtains only the exact bounded public FAA research sample (27,291,624 bytes).

## Distribution gate is closed

The package's NuGet MIT expression describes the wrapper; it does not establish obligations for every
bundled DLL. Upstream commit `55ffd9b17c9fadf9aadf3d21da0ed1e0c3f8bd00` collects all build DLLs and
Visual C++ runtime DLLs. Its pinned vcpkg baseline `1391150599c0b9dc6f2a3c79c50eff6c94b077d1`
identifies **Poppler 26.4.0** and **libmysql 8.0.46** as **GPL-2.0-or-later**. The Windows package also
contains `msodbcsql17.dll`; the runtime package has no complete component notice/version inventory.
Do not ship this bundle as an MIT-only dependency or silently change AeroScience's license.

Recommended production path: build a reproducible reduced GDAL/PROJ/NetCDF/HDF/TIFF stack excluding
unneeded GPL/proprietary drivers, record every native component's exact version/hash/license and
redistribution conditions, bundle required notices, then repeat actual Windows/Linux loading and
self-contained isolation checks. A different proven decoder may be selected after equivalent review.
Native load success alone does not clear this gate. No installed SDK/MSFS/device acceptance is claimed.

Sources: [wrapper](https://github.com/MaxRev-Dev/gdal.netcore/tree/55ffd9b17c9fadf9aadf3d21da0ed1e0c3f8bd00),
[pinned Poppler manifest](https://github.com/microsoft/vcpkg/blob/1391150599c0b9dc6f2a3c79c50eff6c94b077d1/ports/poppler/vcpkg.json),
[pinned MySQL manifest](https://github.com/microsoft/vcpkg/blob/1391150599c0b9dc6f2a3c79c50eff6c94b077d1/ports/libmysql/vcpkg.json).
