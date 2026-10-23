"""Independent owner-side NASA ENVI comparison; no authorization or publication claim.

Read the actual retained source locally. Never commit protected files or raw spectra.
"""
import argparse
from datetime import datetime,timezone
import hashlib
import json
from pathlib import Path
import numpy as np
import rasterio
from rasterio.warp import transform


def verify(folder,originals):
    folder=Path(folder);originals=Path(originals)
    value=json.loads((folder/'nasa-original-samples.json').read_text())
    if value['evidenceType']!='owner-supplied-retained-nasa-source' or value['family'] not in ('classic','ng') or value['flightObservationsCreated']!=0:raise ValueError('Expected actual owner-side local NASA source samples.')
    source=value['source'];bands=source['manifest']['bands'];expected=224 if value['family']=='classic' else len(bands)
    if len(bands)!=expected or value['family']=='ng' and expected not in (425,432):raise ValueError('Unsupported family-specific spectral dimensions.')
    hashes=[]
    for f in source['files']:
        path=(originals/f['relativePath']).resolve()
        if not path.is_relative_to(originals.resolve()) or path.stat().st_size!=f['bytes']:raise ValueError('Original path/size changed.')
        with path.open('rb') as stream:actual=hashlib.file_digest(stream,'sha256').hexdigest()
        if actual!=f['sha256']:raise ValueError('Original hash changed.')
        hashes.append(actual)
    checked=0;missing=0
    with rasterio.open(originals/bands[0]['file']) as dataset:
        if dataset.count!=len(bands):raise ValueError('Original cube has different spectral dimensions.')
        header=dataset.tags(ns='ENVI');wavelengths=[float(v) for v in header['wavelength'].strip('{}').split(',')]
        units=header['wavelength_units'].casefold();factor=1000 if units in ('micrometers','um') else 1 if units in ('nanometers','nm') else None
        if factor is None:raise ValueError('Unknown original wavelength units.')
        points=value['points'];x,y=transform('EPSG:4326',dataset.crs,[p['longitude'] for p in points],[p['latitude'] for p in points])
        grid=next(iter(source['grids'].values()))
        if str(dataset.crs)!=grid['crs'] or (dataset.width,dataset.height)!=(grid['width'],grid['height']):raise ValueError('Original CRS/dimensions differ.')
        np.testing.assert_allclose(tuple(dataset.transform)[:6],[grid['pixelWidth'],grid['rowX'],grid['originX'],grid['columnY'],grid['pixelHeight'],grid['originY']],rtol=0,atol=1e-9)
        for b,s in zip(bands,value['samples'],strict=True):
            if b['channel']!=s['channel'] or b['wavelengthNm']!=wavelengths[b['channel']]*factor:raise ValueError('Detector channel/wavelength mismatch.')
            samples=list(dataset.sample(zip(x,y),indexes=b['channel']+1,masked=True))
            for native,observed in zip(samples,s['values'],strict=True):
                raw=float(native[0]) if not np.ma.is_masked(native[0]) else np.nan
                unavailable=b['badBand'] or not np.isfinite(raw) or raw==b.get('noData')
                if unavailable:
                    if observed is not None:raise ValueError('Bad-band/nodata source presented as valid.')
                    missing+=1
                else:
                    np.testing.assert_allclose(raw*b['scale']+b['offset'],observed,rtol=0,atol=1e-9);checked+=1
    if not checked:raise ValueError('No original numeric values were compared.')
    return {'evidenceType':'independent-owner-NASA-numeric-comparison','family':value['family'],'checkedUtc':datetime.now(timezone.utc).isoformat(),'scene':source['manifest']['sceneId'],'observedUtc':source['manifest']['observationUtc'],'bands':len(bands),'numericComparisons':checked,'missingOrBadBandComparisons':missing,'sourceHashes':hashes,'maximumAllowedAbsoluteDifference':1e-9,'authorization':'Not established by this local check; retain official authorized acquisition evidence separately.','windowsMsfs':'not tested'}


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('--folder',type=Path,required=True);parser.add_argument('--originals',type=Path,required=True);args=parser.parse_args()
    result=verify(args.folder,args.originals);(args.folder/'independent-nasa-comparison.json').write_text(json.dumps(result,indent=2)+'\n')
    print('Independent '+result['family']+' original comparison passed; owner authorization and installed acceptance remain separate.')
