"""Independent developer-only GDAL check of the genuine Cuprite import spectrum."""
import argparse
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import numpy as np
import rasterio
from rasterio.warp import transform


def verify(folder):
    folder = Path(folder)
    actual = json.loads((folder / 'guided-envi-acceptance.json').read_text())
    if actual['evidenceType'] != 'genuine-downloaded-envi' or actual['windowsMsfs'] != 'not tested':
        raise ValueError('Expected explicitly genuine source / synthetic flight evidence')
    source = actual['source']
    root = folder / 'ScienceLibrary' / source['relativeDirectory']
    for f in source['files']:
        with (root / f['relativePath']).open('rb') as stream:
            if hashlib.file_digest(stream, 'sha256').hexdigest() != f['sha256']:
                raise ValueError('Original integrity mismatch')
    differences = []
    checked = 0
    definitions = source['manifest']['bands']
    with rasterio.open(root / definitions[0]['file']) as dataset:
        center = actual['center']
        x, y = transform('EPSG:4326', dataset.crs, [center['longitude']], [center['latitude']])
        for original, decoded in zip(definitions, actual['spectrum'], strict=True):
            if original['channel'] != decoded['channel'] or original['wavelengthNm'] != decoded['wavelengthNm']:
                raise ValueError('Original channel/wavelength changed')
            if original['badBand']:
                if decoded['value'] is not None:
                    raise ValueError('Bad band was presented as valid')
                continue
            raw = next(dataset.sample([(x[0], y[0])], indexes=original['channel'] + 1))[0]
            value = float(raw) * original['scale'] + original['offset']
            if decoded['value'] is None or not np.isfinite(value):
                raise ValueError('Valid original sample missing')
            differences.append(abs(value - decoded['value']))
            checked += 1
    if checked != 170 or max(differences) != 0:
        raise ValueError('Genuine numeric spectrum differs from independent original read')
    return dict(evidenceType='independent-genuine-envi-comparison', checkedUtc=datetime.now(timezone.utc).isoformat(),
                tool=f'rasterio {rasterio.__version__} / GDAL {rasterio.__gdal_version__}', checkedGoodBands=checked,
                excludedBadBands=224 - checked, maximumAbsoluteDifference=max(differences),
                windowsMsfs='not tested', sourceObservedUtc=source['manifest']['observationUtc'])


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--folder', type=Path, required=True)
    args = parser.parse_args()
    result = verify(args.folder)
    (args.folder / 'independent-envi-comparison.json').write_text(json.dumps(result, indent=2) + '\n')
    print('170 actual source bands match independent GDAL reads; 54 bad bands excluded. No Windows/MSFS claim.')
