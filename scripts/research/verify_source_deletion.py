"""Independently compare retained genuine 3DEP samples after controlled deletion.

Rasterio/numpy are development verification tools, never application dependencies.
The input Library is read-only; no deleted original is reconstructed or restored.
"""
import argparse
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path

import numpy as np
import rasterio
from rasterio.warp import transform


def verify(folder, original_library):
    folder, original_library = Path(folder), Path(original_library)
    evidence = json.loads((folder / 'source-deletion.json').read_text(encoding='utf-8'))
    source = evidence['originalSource']
    if source['manifest']['acquisition']['workflow'] != 'usgs-3dep-numeric':
        raise ValueError('This acceptance comparison supports genuine 3DEP elevation only.')
    root = original_library / source['relativeDirectory']
    for original in source['files']:
        path = root / original['relativePath']
        with path.open('rb') as stream:
            assert hashlib.file_digest(stream, 'sha256').hexdigest() == original['sha256']
        assert path.stat().st_size == original['bytes']
    checks = 0
    for band in source['manifest']['bands']:
        observed = next(value for value in evidence['bands'] if value['id'] == band['id'])
        assert observed['unit'] == band['unit']
        with rasterio.open(root / band['file']) as raster:
            grid = source['grids'][band['id']]
            assert str(raster.crs) == grid['crs']
            assert (raster.width, raster.height) == (grid['width'], grid['height'])
            np.testing.assert_allclose(tuple(raster.transform)[:6], [grid['pixelWidth'], grid['rowX'], grid['originX'], grid['columnY'], grid['pixelHeight'], grid['originY']], rtol=0, atol=1e-9)
            x, y = transform('EPSG:4326', raster.crs, [p['longitude'] for p in evidence['locations']], [p['latitude'] for p in evidence['locations']])
            data = raster.read(band['channel'] + 1)
            expected = []
            for a, b in zip(x, y):
                column, row = (~raster.transform) * (a, b)
                column = int(np.floor(round(column) if abs(column-round(column)) < 1e-8 else column))
                row = int(np.floor(round(row) if abs(row-round(row)) < 1e-8 else row))
                value = float(data[row, column]) if 0 <= row < raster.height and 0 <= column < raster.width else np.nan
                if value in (raster.nodata, band.get('noData')):
                    value = np.nan
                expected.append(value * band['scale'] + band['offset'])
            np.testing.assert_allclose([np.nan if value is None else value for value in observed['values']], expected, rtol=0, atol=1e-9, equal_nan=True)
            checks += len(expected)
    for key in ('originalsRemoved', 'offlineSavedValuesIdentical', 'savedMapAndPackageOpened', 'removedSourceNotSelectableForNewSampling', 'collectedSciencePurged', 'controlledRecoveryCopyPurged', 'inputOriginalsPreserved'):
        assert evidence[key] is True, key
    result = {'checkedUtc': datetime.now(timezone.utc).isoformat(), 'passed': True, 'numericComparisons': checks, 'method': 'Independent rasterio native original samples/CRS/affine/nodata/scale against hash-verified retained values; synthetic flight inputs, not MSFS acceptance', 'originalFiles': source['files'], 'windowsMsfs': 'not tested'}
    (folder / 'independent-deletion.json').write_text(json.dumps(result, indent=2) + '\n', encoding='utf-8')
    return result


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('folder')
    parser.add_argument('original_library')
    args = parser.parse_args()
    print(json.dumps(verify(args.folder, args.original_library), indent=2))
