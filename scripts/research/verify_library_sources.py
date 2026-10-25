"""Independent GDAL/rasterio validation of genuine Library downloads (development only).
Requires rasterio/numpy; these are never required from AeroScience pilots.
"""
import argparse
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import numpy as np
import rasterio
from rasterio.warp import transform


def verify(folder,providers=False,reuse=False):
    folder = Path(folder)
    evidence = json.loads((folder/('provider-acceptance.json' if providers else 'library-reuse.json' if reuse else 'library.json')).read_text(encoding='utf-8'))
    if reuse:
        assert evidence['observedInitialTransferBytes']>0 and evidence['repeatedTransferBytes']==0
        assert evidence['repeat']['state']=='Completed' and len({w['reuseSourceId'] for w in evidence['repeat']['preview']['work']})==2
    results = []
    for item in evidence['results']:
        if providers:item={**item,'provider':item['workflow']}
        source = item['source']
        root = folder/'ScienceLibrary'/source['relativeDirectory']
        for original in source['files']:
            path = root/original['relativePath']
            with path.open('rb') as stream:
                assert hashlib.file_digest(stream, 'sha256').hexdigest() == original['sha256']
            assert path.stat().st_size == original['bytes']
        values = {}
        for band in source['manifest']['bands']:
            with rasterio.open(root/band['file']) as raster:
                x, y = transform('EPSG:4326', raster.crs,
                                 [p['longitude'] for p in item['locations']],
                                 [p['latitude'] for p in item['locations']])
                # The existing reader snaps coordinates within 1e-8 pixel of an exact
                # boundary to that boundary before floor. This regular acceptance grid
                # hits Copernicus boundaries exactly; GDAL's unsnapped floating floor
                # can otherwise choose the preceding row/column. Preserve strict values.
                pixels = [(~raster.transform)*(a, b) for a, b in zip(x, y)]
                rows = [(int(np.floor(round(row) if abs(row-round(row)) < 1e-8 else row)),
                         int(np.floor(round(col) if abs(col-round(col)) < 1e-8 else col))) for col, row in pixels]
                data = raster.read(band['channel']+1)
                raw = np.array([float(data[r, c]) if 0 <= r < raster.height and 0 <= c < raster.width else np.nan for r, c in rows])
                for nodata in (raster.nodata, band.get('noData')):
                    if nodata is not None:
                        raw[raw == nodata] = np.nan
                raw[[r < 0 or c < 0 or r >= raster.height or c >= raster.width for r, c in rows]] = np.nan
                values[band['id']] = raw*band['scale']+band['offset']
                grid = source['grids'][band['id']]
                assert str(raster.crs) == grid['crs']
                assert (raster.width, raster.height) == (grid['width'], grid['height'])
                assert np.allclose(tuple(raster.transform)[:6], [grid['pixelWidth'], grid['rowX'], grid['originX'], grid['columnY'], grid['pixelHeight'], grid['originY']], rtol=0, atol=1e-9)
        kind = source['manifest']['kind']
        if kind == 'thermal':
            qa = values['QA_PIXEL']
            good = np.isfinite(qa) & (qa == np.floor(qa)) & (qa >= 0) & (qa <= 65535)
            good &= (np.nan_to_num(qa).astype(np.int64) & 63) == 0
            good &= (values['ST_B10'] >= 100) & (values['ST_B10'] <= 500)
            if 'QA_RADSAT' in values:
                good &= values['QA_RADSAT'] == 0
            values['ST_B10'] = np.where(good, values['ST_B10'], np.nan)
            values['temperature'] = values['ST_B10']
        elif kind == 'multispectral':
            good = np.isin(values['SCL'], [2, 4, 5, 6, 7])
            for name in values:
                if name != 'SCL':
                    values[name] = np.where(good, values[name], np.nan)
            den = values['B08']+values['B04']
            with np.errstate(divide='ignore', invalid='ignore'):
                ndvi = (values['B08']-values['B04'])/den
            values['ndvi'] = np.where((np.abs(den) > 1e-12) & (ndvi >= -1) & (ndvi <= 1), ndvi, np.nan)
        verified = 0
        for band in item['bands']:
            expected = np.array([np.nan if n is None else n for n in band['values']])
            np.testing.assert_allclose(values[band['id']], expected, rtol=0, atol=1e-9, equal_nan=True)
            verified += len(expected)
        for name, recorded in item['derived'].items():
            expected = np.array([np.nan if n is None else n for n in recorded])
            np.testing.assert_allclose(values[name], expected, rtol=0, atol=1e-9, equal_nan=True)
            verified += len(expected)
        results.append(dict(provider=item['provider'], scene=source['manifest']['sceneId'], comparisons=verified,
                            valid_derived_values=item['valid'], source_hashes=[f['sha256'] for f in source['files']]))
    return dict(checked_utc=datetime.now(timezone.utc).isoformat(), method='Independent GDAL/rasterio CRS, affine, native numeric scaling and QA comparison; documented 1e-8-pixel boundary snap, unchanged 1e-9 value tolerance', results=results)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('folder', type=Path)
    parser.add_argument('--providers',action='store_true',help='Check actual automatic-provider run receipts with the same independent numeric/QA rules.')
    parser.add_argument('--reuse',action='store_true',help='Verify genuine cumulative Library windows at the same unchanged numeric/CRS tolerances.')
    args = parser.parse_args()
    if args.providers and args.reuse:parser.error('Choose one evidence workload.')
    result = verify(args.folder,args.providers,args.reuse)
    (args.folder/('independent-numeric-comparison.json' if args.providers else 'independent-reuse.json' if args.reuse else 'independent-library.json')).write_text(json.dumps(result, indent=2)+'\n', encoding='utf-8')
    print(json.dumps(result, indent=2))
