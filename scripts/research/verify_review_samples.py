"""Independent GDAL check of genuine retained-source v0.4.1 review samples.

Developer-only rasterio/numpy dependencies. Does not establish new provider-network
or Windows/MSFS acceptance and never changes original scientific files.
"""
import argparse
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import numpy as np
import rasterio
from rasterio.warp import transform


def digest(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def verify(folder):
    summary = json.loads((folder / 'review-acceptance.json').read_text())
    if summary['evidenceType'] != 'genuine-retained-data-scientific-review' or summary['windowsMsfs'] != 'not tested':
        raise ValueError('Expected retained genuine data, synthetic flight and no hardware claim')
    results = []
    for item in summary['results']:
        root = folder / item['kind'].capitalize()
        report = json.loads((root / 'review-acceptance.json').read_text())
        source = report['source']
        library = root / 'ScienceLibrary'
        original_root = library / source['relativeDirectory']
        for original in source['files']:
            path = original_root / original['relativePath']
            if path.stat().st_size != original['bytes'] or digest(path) != original['sha256']:
                raise ValueError('Genuine original integrity mismatch')
        points = report['sampleLocations']
        expected = {}
        for band in source['manifest']['bands']:
            with rasterio.open(original_root / band['file']) as dataset:
                x, y = transform('EPSG:4326', dataset.crs, [p['longitude'] for p in points], [p['latitude'] for p in points])
                values = np.array([v[0] for v in dataset.sample(zip(x, y), indexes=band['channel'] + 1)], dtype=np.float64)
                for nodata in (dataset.nodata, band.get('noData')):
                    if nodata is not None:
                        values[values == nodata] = np.nan
                indices = [dataset.index(a, b) for a, b in zip(x, y)]
                values[[r < 0 or c < 0 or r >= dataset.height or c >= dataset.width for r, c in indices]] = np.nan
                expected[band['id']] = values * band['scale'] + band['offset']
                saved_grid = next(v['originalGrid'] for v in report['bands'] if v['id'] == band['id'])
                saved_affine = [saved_grid['pixelWidth'], saved_grid.get('rowX', 0), saved_grid['originX'], saved_grid.get('columnY', 0), saved_grid['pixelHeight'], saved_grid['originY']]
                if not np.allclose(saved_affine, list(dataset.transform)[:6], atol=1e-10, rtol=0) or saved_grid['crs'] != dataset.crs.to_string():
                    raise ValueError('Native affine/CRS mismatch')
        kind = source['manifest']['kind']
        derived = {}
        if kind == 'thermal':
            qa = expected['QA_PIXEL']
            temperature = expected['ST_B10']
            valid = np.isfinite(qa) & (qa >= 0) & (qa <= 65535) & (qa == np.floor(qa)) & ((np.nan_to_num(qa).astype(np.int64) & 63) == 0)
            valid &= (temperature >= 100) & (temperature <= 500)
            if 'QA_RADSAT' in expected:
                valid &= expected['QA_RADSAT'] == 0
            expected['ST_B10'] = np.where(valid, temperature, np.nan)
            derived['temperature'] = expected['ST_B10'].copy()
        elif kind == 'multispectral':
            quality = expected['SCL']
            valid = np.isfinite(quality) & (quality == np.floor(quality)) & (quality >= 0) & (quality <= 11) & ~np.isin(quality, [0, 1, 3, 8, 9, 10, 11])
            for name in expected:
                if name != 'SCL':
                    expected[name] = np.where(valid, expected[name], np.nan)
            red, nir = expected['B04'], expected['B08']
            denominator = red + nir
            with np.errstate(divide='ignore', invalid='ignore'):
                ndvi = (nir - red) / denominator
            derived['ndvi'] = np.where((np.abs(denominator) > 1e-12) & (ndvi >= -1) & (ndvi <= 1), ndvi, np.nan)
        elif kind == 'terrain':
            derived['elevation'] = next(iter(expected.values())).copy()
        else:
            raise ValueError('No hyperspectral claim is established by this retained-source check')
        maximum = 0.
        for name, actual in [(b['id'], b['values']) for b in report['bands']] + list(report['derived'].items()):
            oracle = derived[name] if name in derived else expected[name]
            actual = np.array(actual, dtype=np.float64)
            if not np.array_equal(np.isnan(actual), np.isnan(oracle)):
                raise ValueError('QA/nodata support mismatch: ' + name)
            finite = np.isfinite(oracle)
            difference = float(np.max(np.abs(actual[finite] - oracle[finite]))) if finite.any() else 0.
            if difference > 1e-9:
                raise ValueError('Genuine numeric sample mismatch: ' + name)
            maximum = max(maximum, difference)
        if report['originalFlight']['timestampUtc'] != report['product']['flightPacketUtc'] or not report['idempotent'] or report['mapCount'] != 1:
            raise ValueError('Flight identity or idempotent recovery proof mismatch')
        results.append(dict(kind=kind,sourceId=source['id'],sourceScene=source['manifest']['sceneId'],sourceObservedUtc=source['manifest']['observationUtc'],productSha256=report['product']['sha256'],samplePositions=len(points),bands=len(expected),maximumAbsoluteDifference=maximum,qaAndNodataSupportMatches=True,nativeAffineAndCrsMatch=True,originalIntegrityVerified=True,idempotent=True))
    return dict(formatVersion=1,evidenceType='independent-genuine-retained-review-comparison',checkedUtc=datetime.now(timezone.utc).isoformat(),windowsMsfs='not tested',newProviderNetworkAcceptance=False,results=results)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--folder', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    result = verify(args.folder)
    args.output.write_text(json.dumps(result, indent=2) + '\n')
    print('Independent genuine retained-data review passed:', len(result['results']), 'kinds; no Windows/MSFS or new network claim.')
