# Automatic scientific-data providers and limits

For the **0.5.0 Geographic Library NASA assistant**, see [Hyperspectral](hyperspectral.md).
It adds real paginated Classic/NG and current EMIT V002 catalog browsing, explicit official
browser access and product-bound compatible AVIRIS ENVI verification/import. Direct protected
NASA downloading and native EMIT decoding are not implemented. The existing automatic
flight-area summary described below remains metadata-only; it does not start protected
NASA transfers. Classic/NG genuine numeric acceptance remains pending authorized original
files; [current evidence and limitations](../research/v0.5.0-nasa-validation.json) are separate
from the historical numeric-provider results below.


## Implemented direct provider paths

| Workflow | Discovery / retrieval | Numeric content and integrity |
|---|---|---|
| USGS 3DEP | Official National Map ImageServer metadata and bounded `exportImage` F32 TIFF | Single bare-earth composite elevation band, metre units, nearest-neighbor bounded 256–768 pixel WGS84 export, preserving the reference sampling density for larger requests; no rendered tiles. Survey dates, native per-pixel GSD and composite vertical reference are unspecified, never invented. All-nodata export falls back. |
| Landsat C2 L2 | Microsoft Planetary Computer STAC `landsat-c2-l2`; public Data Authentication SAS signing in memory | Landsat 8/9 L2SP only; native-DN ST_B10, QA_PIXEL, QA_RADSAT from one scene; 0.00341802 × DN + 149 K; fill/cloud/cirrus/shadow/snow/saturation excluded by production sampling. |
| Sentinel-2 L2A | Element 84 Earth Search v1 STAC, anonymous `sentinel-cogs` HTTPS assets | B02/B03/B04/B08 reflectance and SCL; 10 m bands / 20 m classification; source-specific scale and additive offset. The provider's `raster:bands` metadata controls scaling, including when `earthsearch:boa_offset_applied` is present. Negative values are not silently clipped. Invalid NDVI remains null. |
| Copernicus GLO-30 | Planetary Computer STAC `cop-dem-glo-30`; public SAS signing | Numeric ~30 m **surface model**, EGM2008 geoid heights; buildings/vegetation can be present. Catalog publication timestamp is not mislabeled as a per-pixel survey date. |
| Hyperspectral discovery | Official NASA CMR for EMITL2ARFL v001, AVIRIS-Classic L2 Reflectance v1, AVIRIS-NG L2 Reflectance v1 | Up to two actual geographic granules per catalog, all archived dates searched and displayed. Discovery only; NASA protected scientific downloads, user authorization and automated NetCDF/ENVI preparation are unsupported in this release. |

Providers are public third-party services, with no uptime guarantee. Appropriate free-data licenses/attribution remain attached to each source. No paid shared key, developer-funded download proxy, mandatory cloud account, central server or synthetic fallback is used. SAS authorization is short-lived and renewed once on expiry; signed URLs/tokens are not written into provenance or logs.

## Bounds and selection

Live prediction begins with latitude-aligned 0.02° areas. Explicit preflight uses bounded 0.04° regions; steady fast observed motion can retain a native 0.04° optical crop with required-area fallback at scene boundaries. Slow/turning motion stays bounded and longitude is not inflated near the poles. Supported bounds are up to 0.06° on each side, 80°S–84°N, without dateline wrapping. Preflight progresses through bounded windows of up to 24 instrument jobs, with 4096 distinct areas/100000 geometry-check limits per session, transfer and storage ceilings, and explicit partial-workload status. Live priority follows actual aircraft motion and measured preparation latency, with at most 128 predicted areas and a 64-job queue. Preparation is never proof of complete scientific coverage.

Satellite discovery considers at most 60 ordered candidates, verifies the entire requested cell against scene polygons, requires complete same-scene bands, and applies the configured observation-date/cloud policy. Default search is 730 days, at most 30% scene cloud, clearest then most recent. Newest acceptable is selectable, with optional preferred latest date. Local QA/nodata is checked independently: scene cloud percentage never guarantees a clear sensor footprint. An imported advanced source may remain usable outside automatic catalog policy; actual source provenance/date are always displayed.

Each prepared band is limited to 2048×2048 and 32 MiB decoded data. Managed TIFF reads use exact HTTP 206 Content-Range/length checks, identity encoding, immutable ETag checks where provided, a 64 KiB range cache (≤16 MiB per asset), ≤2048 requests/asset, and an overall per-job network budget (default 64 MiB; configurable 1–512 MiB). Full-object responses to range requests are refused. No overview substitution, scientific RGB screenshot, radiometric resampling or invented detail is used. The bounded crop preserves original numeric type/DNs, scale, offset, grid, nodata and quality masks. Exact pixel-boundary sampling uses a stable east/south tie within 1e-8 pixel, so floating-point origin changes from clipping cannot switch to an adjacent pixel.

All transport uses TLS verification, fixed allowlisted HTTPS provider endpoints, no redirects/cookies, application identification, 45-second request timeouts, paced requests and limited retries for transient failures/429. Provider Retry-After is honored; a long requested wait becomes a clear retry-later state. Preparation has a 12-minute cancellation timeout. Concurrent preparations are limited to 1–2. User cancellation and preference changes cancel active and queued preparations; no missing science observations are backfilled.

## Privacy, bandwidth and original data

Scientific consent defaults off and remains separate from OSM. Onboarding and Settings offer offline-only before any geographic scientific query. Windows Network List Manager cost detection supports unmetered-only access; unknown or constrained cost pauses it. Paired browser clients share the companion's settings/status; access configuration stays restricted to the local PC.

Conservative source/staging reservations are checked against the Science Library cap and free disk space before writing scientific assets. The importer additionally reserves its verified-copy budget, so displayed staging bounds exclude that additional import reservation. Near capacity, preparation can stop even before the used-space meter reaches its limit. Nothing deletes originals to make room. Downloads become retained, hashed **Sources**, not cleanup-eligible caches; they remain needed to reproduce completed products. A successfully verified import removes only its duplicated owned preparation files. Interrupted staging remains ledger-tracked and accounted for explicit review/recovery; Retry uses a new isolated staging folder and never replaces an original. Restarts do not resume network transfers without fresh consent/policy checks.

## Genuine acceptance versus mocks

**Final numeric acceptance passed (2026-10-18):** a fresh complete online run validated all four numeric workflows and persisted/reloaded their mission products. USGS, Landsat and GLO-30 each produced 576 valid derived samples; Sentinel produced 183 valid NDVI samples from 576 checked positions. Independent GDAL comparison matched all 5,329 retained GLO-30 pixels and 576 source samples exactly. NASA discovery returned six matching catalog observations. The earlier HTTP 503 interruption is resolved for this run. The release build still checks source hashes and genuine evidence; Windows/MSFS and installer execution remain separate acceptance.

The [genuine provider report](../research/v0.3.1-automatic-provider-acceptance.json) records real discovery, bounded retrieval, automatically generated manifests, existing-library import, QA-aware sampling and persisted/reloaded mission products for all four numeric paths. Flight inputs are explicitly synthetic WGS84 positions, **not MSFS flights**. Independent GDAL/rasterio source comparisons are development checks, not runtime dependencies. Recorded STAC fixtures and synthetic TIFF/HTTP tests exercise parser/failure behavior only and are not counted as online acceptance.

Maintainers can reproduce the explicit opt-in network check with `dotnet run --project tests/AeroScience.ProviderAcceptance -- --network --output <empty-local-folder>`. This is a developer test, never an ordinary pilot installation step. Use a bounded test area and respect provider quotas. Failures remain failures; the harness does not substitute fixtures.

## Authoritative references

- [USGS 3DEP numeric service](https://elevation.nationalmap.gov/arcgis/rest/services/3DEPElevation/ImageServer), [ArcGIS exportImage numeric parameters](https://developers.arcgis.com/rest/services-reference/enterprise/export-image/).
- [USGS Landsat surface temperature](https://www.usgs.gov/landsat-missions/landsat-collection-2-surface-temperature), [Planetary Computer STAC](https://planetarycomputer.microsoft.com/docs/reference/stac/), [SAS authentication](https://planetarycomputer.microsoft.com/docs/concepts/sas/).
- [Element 84 Sentinel-2 documentation, scaling and known issues](https://github.com/Element84/earth-search/blob/main/docs/collections/sentinel-2-l2a.md), [Copernicus L2A description](https://sentiwiki.copernicus.eu/web/s2-products).
- [Copernicus DEM](https://spacedata.copernicus.eu/collections/copernicus-digital-elevation-model), [Planetary Computer GLO-30 metadata/license](https://planetarycomputer.microsoft.com/api/stac/v1/collections/cop-dem-glo-30).
- [NASA CMR API](https://cmr.earthdata.nasa.gov/search/site/docs/search/api.html), [EMIT access](https://lpdaac.usgs.gov/products/emitl2arflv001/), [AVIRIS-Classic reflectance](https://doi.org/10.3334/ORNLDAAC/2154), [AVIRIS-NG reflectance](https://doi.org/10.3334/ORNLDAAC/2110), [official Earthdata authorization](https://urs.earthdata.nasa.gov/documentation/for_users/user_token).

Advanced/local hyperspectral import uses the [guided local ENVI workflow](using-your-own-hyperspectral-dataset.md). NASA catalog discovery remains optional information only and never starts a protected spectral download. Scientific source modes, per-preparation and rolling-hour budgets are described in [Scientific Data Sources](scientific-data-sources.md).


## v0.3.2 evidence and download policies

[Current v0.3.2 online evidence](../research/v0.3.2-automatic-provider-acceptance.json) now passes for all four numeric workflows in the maintainer’s committed report. Its scientific-source hashes validate; test-fixture cleanup does not change them. Earlier cloud [intermediate evidence](../research/v0.3.2-automatic-provider-intermediate-evidence.json), [retained-source validation](../research/v0.3.2-offline-provider-validation.json) and [guided ENVI validation](../research/v0.3.2-guided-envi-acceptance.json) remain separate. Regional before/after efficiency and installed Windows/MSFS acceptance remain outstanding.

Saving a plan no longer downloads automatically. Review and explicitly start its preparation. Preflight has a separate finite/Unlimited session volume; it does not debit the in-flight rolling-hour allowance. The new-install defaults are 1 GiB preflight session / 256 MiB per flight rolling application hour. Existing finite preferences are preserved. Actual consumed payload, held read reservations and available allowance appear in Download Activity, alongside a combined actual transfer total. These are volume controls, not connection-speed throttles. Other safeguards remain under Unlimited. Usage counters are application-session state; preferences persist.

Validated original regions can serve adjacent cells and later missions. Short-lived catalog caches and ETag-validated native COG range reuse reduce repeat work without conflating scenes or dropping masks. Metadata/range caches are bounded; verified scientific originals are not disposable caches. Download Activity shows unknown size/time when no defensible total exists.
