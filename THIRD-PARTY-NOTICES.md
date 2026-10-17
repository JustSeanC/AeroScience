# Third-party components

The AeroScience license covers original project code, not Microsoft SDK files or third-party runtimes.

- .NET / ASP.NET Core and Microsoft.Data.Sqlite: Microsoft / .NET Foundation, MIT and associated runtime notices. A self-contained publish retains its runtime `LICENSE.txt` and `ThirdPartyNotices.txt` when supplied by the runtime pack.
- SQLitePCLRaw (Eric Sink / contributors): Apache-2.0. See <https://github.com/ericsink/SQLitePCL.raw>. Its native SQLite distribution is governed by the supplied package notices; SQLite itself is public domain (<https://sqlite.org/copyright.html>).
- Playwright: Microsoft, Apache-2.0; development/test tooling, not a runtime dependency of the companion. Test packages are not included as application dependencies.
- MSFS SDK, SimConnect runtime, and simulator-owned JS/templates: Microsoft proprietary terms apply. No SDK binary or simulator-owned script/template is checked into this repository or included in the companion preview. Review the installed official SDK terms before redistributing any runtime file.
- Inno Setup: Copyright Jordan Russell and Martijn Laan; <https://jrsoftware.org/>. The official compiler is a maintainer build prerequisite. Its generated setup/uninstaller retain the framework's notices. See <https://github.com/jrsoftware/issrc/blob/main/license.txt> and the installed compiler's license; AeroScience does not claim authorship of the installation framework.
- Microsoft Visual C++ v14 x64 Redistributable: Microsoft proprietary runtime, downloaded from Microsoft's official distribution endpoint, verified with Windows Authenticode and bundled as the unmodified Microsoft installer. Microsoft's installer applies its runtime license and supported installation behavior. See <https://learn.microsoft.com/en-us/cpp/windows/redistributing-visual-cpp-files> and the installed Visual Studio Build Tools REDIST/license terms. The shared prerequisite is not removed when AeroScience is uninstalled.

SDK research also consulted Microsoft's published avionics types and community examples linked in `docs/toolbar.md`. No external application implementation was vendored. The project loads the simulator's own templates and CommBus service at runtime; those files are not rehosted by AeroScience.

## Offline mapping, charts and geodesy added in v0.2.1

- Leaflet **1.9.4**, BSD 2-Clause, Copyright Vladimir Agafonkin and contributors. Bundled locally with its complete license in `wwwroot/vendor/leaflet/LICENSE.txt`. <https://leafletjs.com/>.
- Leaflet.draw **1.0.4**, MIT, Copyright Jacob Toye and Leaflet.draw contributors. The npm tarball omits its license: the unchanged license from tag `v1.0.4` is bundled in `wwwroot/vendor/leaflet-draw/LICENSE.txt`. <https://github.com/Leaflet/Leaflet.draw/blob/v1.0.4/MIT-LICENSE.md>.
- Chart.js **4.5.1**, MIT, Copyright Chart.js contributors. Bundled locally with its license in `wwwroot/vendor/chart.js/LICENSE.txt`. <https://www.chartjs.org/>.
- GeographicLib.NET **2.3.2**, MIT, Copyright Charles Karney and GeographicLib.NET contributors; pure .NET WGS84 geodesics. Complete license in `wwwroot/vendor/geographiclib/LICENSE.txt`. NuGet lock files pin package integrity. <https://github.com/noelex/GeographicLib.NET>.
- Natural Earth vector geography is **public domain**: countries (1:110m), populated places (1:110m), airports (1:10m), pinned to upstream commit `ca96624a56bd078437bca8184e78163e5039ad19`. Only descriptive property selection was changed; original coordinates are retained. Provenance, upstream/bundled SHA-256 hashes and feature counts are in `wwwroot/geography/provenance.json`. This is coarse geographic reference, not certified aviation navigation. <https://www.naturalearthdata.com/about/terms-of-use/>.

Default mapping uses these bundled vector datasets and makes no external map request. The optional online detail layer uses OpenStreetMap standard tiles only after explicit per-map consent and displays the required attribution. It performs no bulk download, prefetch or offline tile-cache construction. Service availability and usage policy apply: <https://operations.osmfoundation.org/policies/tiles/> and <https://www.openstreetmap.org/copyright>. A user's selection discloses the visible region and IP address to that provider. No provider key, account, subscription or internet connection is needed for offline planning/review.

The web library versions and npm integrity hashes are locked. Maintainers can reproduce the vendored JS/CSS/images with `npm ci --ignore-scripts` and `node scripts/build/vendor-web.mjs`; verify them with `python scripts/build/verify_web_assets.py`. Licenses and geographic provenance are included in the installed application and protected by `assets-manifest.json`.
