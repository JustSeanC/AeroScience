# Scientific Data Sources

Open **Settings → Scientific Data Sources** in the desktop app or dashboard. Each section shows its source mode, retained dataset count and size, names, provider, original date, availability, details and geographic coverage. Native Settings and paired browsers read the same source preferences. Imports run on the companion PC using native file/folder pickers.

For terrain, thermal and multispectral select **Automatic**, **My Datasets Only**, or **Prefer My Datasets, Allow Automatic Fallback** independently. Automatic reuses suitable retained sources and favors provider-prepared sources; Prefer My Datasets puts compatible user imports first. My Datasets Only never starts a provider download for that instrument. Hyperspectral uses local datasets only. Changing modes does not switch science recording on/off or delete anything. Excluding a dataset from active selection preserves its original files, provenance and completed products; deletion remains a separate protected Data Management operation.

Automatic discovery/downloads require separate scientific-data privacy consent, network permission and sufficient transfer/storage budgets. Providers receive geographic queries and your IP address. Map consent is independent. Imported local data and archives work offline. Historical source dates are never presented as current aircraft measurement times.

## Terrain

**Automatic:** bounded numeric USGS 3DEP elevation in supported US regions; Copernicus GLO-30 global DSM fallback. DSM includes buildings/vegetation; dates, vertical datums and provider resolution limitations remain explicit. This is not aircraft LiDAR/radar.

**Import Dataset:** choose a compatible **single-band numeric DEM GeoTIFF**. Confirm documented metre units and vertical datum (or explicitly Unknown) when the file does not supply them. Optional source identity, date and usage terms remain unknown unless supplied. The graphical wizard generates the manifest. RGB hillshade/map tiles and unsupported/rotated GeoTIFF grids are rejected. Obtain numeric elevation from [USGS National Map](https://apps.nationalmap.gov/downloader/) or an appropriately licensed Copernicus product. View Coverage and source details before flying.

## Thermal

**Automatic:** Landsat 8/9 Collection 2 Level-2 **L2SP** surface temperature with `ST_B10`, `QA_PIXEL` and applicable `QA_RADSAT`. The provider-delivered 30 m grid is not 30 m native thermal detector detail.

**Import Dataset:** choose one folder containing same-scene numeric **ST_B10 and QA_PIXEL GeoTIFFs**, and QA_RADSAT when supplied. Include the original `*_MTL.json` for automatic scene/date/spacecraft/product inspection. Without that metadata the wizard requires the genuine L2SP scene identifier; do not combine scenes. The fixed official temperature scale is `0.00341802 × DN + 149 K`. QA remains unscaled. Original metadata files are retained with the imported collection. Use [USGS EarthExplorer](https://earthexplorer.usgs.gov/) or the already-supported public provider for legitimate products. A rendered thermal JPG cannot be imported as numeric science.

## Multispectral

**Automatic:** Sentinel-2 Level-2A historical reflectance via supported Earth Search public COGs, with B02/B03/B04/B08 and SCL. Source-specific scale/additive offsets and separate SCL grids are retained; RGB and NDVI use these numeric inputs.

**Import Dataset:** choose a folder containing compatible numeric **B02, B03, B04, B08 and SCL GeoTIFFs from one scene**. Original `MTD_MSIL2A.xml` supplies quantification and BOA offsets when available. Otherwise enter the original documented scale, additive offset and common scene identifier. Do not guess offsets from a processing date. Public COGs are available through [Element 84 Earth Search](https://earth-search.aws.element84.com/v1). Copernicus products containing JP2 require a separate advanced conversion workflow; this release does not pretend to import JP2 directly. No fake QA or reflectance calibration is generated.

## Hyperspectral

**User datasets only:** compatible georeferenced ENVI header/binary cubes. No automatic NASA hyperspectral downloader, account or paid service is required. Dataset Required, Outside Coverage and Invalid explain the next action; a compatible local source participates in predictive readiness without network traffic. Current acquisition may still be unavailable because of nodata, masks, attitude or footprint.

Use [Using Your Own Hyperspectral Dataset in AeroScience](using-your-own-hyperspectral-dataset.md) for a verified genuine Cuprite example and the four-step graphical workflow.

## After import

Check **Dataset Ready**, **View Coverage**, original date, source units, native grids and warnings. Within a recording, valid samples appear in Science Collection. Missing science never stops normal flight recording, blocks other instruments or becomes invented/backfilled data. Both import routes publish through the existing atomic verified source library. Experts retain the optional [JSON-manifest import](sources.md).


## v0.3.2 collections and download controls

Online scientific access has a primary control at the top. Each compatible original source collection can be expanded into pages of twenty retained regions. Collection coverage shows individual georeferenced outlines, preserving gaps and source identities; an outline does not prove valid pixels throughout. Excluding a source changes future selection and does not delete its originals or rewrite old products.

Download Activity separates actual consumed bytes, currently reserved reads and available allowance. In-flight defaults to 256 MiB per rolling application hour; preflight has a separate 1 GiB session-volume default. Existing explicit settings are preserved. Either aggregate policy may be explicitly Unlimited, with substantial-data-use warning; per-preparation, concurrency, provider, consent and storage restrictions remain. Counters measure application payload and exclude network-adapter overhead. Preferences persist; usage ledgers are application-session state.
