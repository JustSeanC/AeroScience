#!/usr/bin/env python3
"""Independent GDAL/OSR original-cell oracle; no downloads or original writes.

Checks the declared deterministic <=256 cells per band, not full-scene QA or
post-flight DSM cutline acceptance. Tolerances fixed before examining values.
"""
import argparse
import hashlib
import json
import math
from pathlib import Path
from osgeo import gdal, osr

if not __debug__:
    raise RuntimeError('Run this independent oracle with assertions enabled; optimized execution cannot provide a pass.')

def fields(v):
    if isinstance(v, dict): return {k[:1].lower()+k[1:]:fields(x) for k,x in v.items()}
    if isinstance(v, list): return [fields(x) for x in v]
    return v

def verify(source, output):
    gdal.UseExceptions()
    read=lambda p:fields(json.loads(p.read_text(encoding='utf-8')))
    metadata=read(output/'source.json'); measured=read(output/'raster-values.json')
    assert measured['schema']=='aeroscience-p08c-retained-raster-1' and measured['originalsUnchanged'] and measured['recordingOrScienceCommands']==measured['networkDownloads']==0
    # Predeclared: exact decoded/scaled values; affine <1e-12 source units;
    # centers <1e-10 degrees. OSR/PROJ is independent of GeographicLib.
    tolerances=dict(nativeValueAbsolute=0,affineSourceUnits=1e-12,pixelCenterDegrees=1e-10)
    originals=[]; checked=0; missing=0; max_geo=0; max_value=0
    for file in metadata['files']:
        path=source/file['relativePath']; sha=hashlib.sha256(path.read_bytes()).hexdigest()
        assert path.stat().st_size==file['bytes'] and sha==file['sha256']
        originals.append(dict(file=file['relativePath'],sha256=sha,bytes=file['bytes']))
    for result in measured['bands']:
        band=result['band']; grid=result['grid']; ds=gdal.Open(str(source/band['file']),gdal.GA_ReadOnly)
        assert [ds.RasterXSize,ds.RasterYSize]==[grid['width'],grid['height']]
        gt=ds.GetGeoTransform(); assert all(abs(x-y)<1e-12 for x,y in zip(gt,[grid[k] for k in ['originX','pixelWidth','rowX','originY','columnY','pixelHeight']]))
        native=ds.GetRasterBand(band.get('channel',0)+1); nodata=native.GetNoDataValue()
        crs=osr.SpatialReference(wkt=ds.GetProjectionRef()); declared=osr.SpatialReference(); declared.SetFromUserInput(grid['crs']); assert crs.IsSame(declared)
        crs.SetAxisMappingStrategy(osr.OAMS_TRADITIONAL_GIS_ORDER); target=osr.SpatialReference(); target.ImportFromEPSG(4326); target.SetAxisMappingStrategy(osr.OAMS_TRADITIONAL_GIS_ORDER); transform=osr.CoordinateTransformation(crs,target)
        total=grid['width']*grid['height']; count=min(256,total); expected=[i*(total-1)//(count-1) if count>1 else 0 for i in range(count)]
        assert [s['row']*grid['width']+s['column'] for s in result['samples']]==expected
        for s in result['samples']:
            raw=float(native.ReadAsArray(s['column'],s['row'],1,1)[0,0]); valid=math.isfinite(raw) and raw not in [nodata,band.get('noData')] and not band.get('badBand')
            value=raw*band.get('scale',1)+band.get('offset',0) if valid else None
            assert s['raw']==(raw if valid else None) and s['value']==value
            if value is None: missing+=1
            else: max_value=max(max_value,abs(s['value']-value))
            x,y=gdal.ApplyGeoTransform(gt,s['column']+.5,s['row']+.5); lon,lat,_=transform.TransformPoint(x,y)
            error=max(abs(lon-s['position']['longitude']),abs(lat-s['position']['latitude'])); assert error<1e-10; max_geo=max(max_geo,error); checked+=1
    receipt=dict(schema='aeroscience-p08c-retained-raster-comparison-1',outcome='Passed',reference='GDAL/OSR '+gdal.VersionInfo('RELEASE_NAME'),tolerances=tolerances,originals=originals,samplesCompared=checked,missingSamples=missing,maximumScaledDifference=max_value,maximumGeographicDifferenceDegrees=max_geo,provider=metadata['manifest']['provider'],scene=metadata['manifest']['sceneId'],sourceDateUtc=metadata['manifest']['observationUtc'],verticalDatum=metadata['manifest']['verticalDatum'],limitations=['Deterministic original cell samples only; not full-scene coverage, derived indices, cutline DSM, or installed acceptance.','QA bands retain original codes; no missing/invalid source made valid.'])
    with (output/'independent-comparison.json').open('x',encoding='utf-8') as f: json.dump(receipt,f,indent=2);f.write('\n')
    print(json.dumps(receipt))

if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('source',type=Path);parser.add_argument('output',type=Path);args=parser.parse_args();verify(args.source,args.output)
