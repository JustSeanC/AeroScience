"""Independent read-only checks of P05d API/export values. Run terrain with
system GDAL, lidar with the pinned laspy/PROJ validation environment.
Both require the explicitly marked genuine-source/synthetic-flight probe.
"""
import argparse, csv, hashlib, json, math, pathlib, datetime

def fields(value):
    if isinstance(value,dict):return {k[:1].lower()+k[1:]:fields(v) for k,v in value.items()}
    if isinstance(value,list):return [fields(v) for v in value]
    return value

def read(path):return fields(json.loads(path.read_text()))

def terrain(root,original,browser):
    from osgeo import gdal
    gdal.UseExceptions();dataset=gdal.Open(str(original),gdal.GA_ReadOnly);band=dataset.GetRasterBand(1);data=band.ReadAsArray();transform=dataset.GetGeoTransform();assert dataset.RasterXSize*dataset.RasterYSize==1024
    values=read(root/'terrain-view-values.json');profile=read(root/'profile.json');manifest=read(root/'terrain-manifest.json');source=profile['source'];assert source['surface']=='composite-dem' and source['unit']=='m' and not source['referenceVerified']
    maximum=0
    for v in values:
        col,row=v['column'],v['row'];raw=float(data[row,col]);assert v['sourceId']==source['id'] and v['quality']=='valid';assert v['value']==raw and v['decodedSourceValue']==raw
        x=transform[0]+(col+.5)*transform[1]+(row+.5)*transform[2];y=transform[3]+(col+.5)*transform[4]+(row+.5)*transform[5];maximum=max(maximum,abs(x-v['position']['longitude']),abs(y-v['position']['latitude']))
        for polygon in v['geometry']['coordinates']:
            for ring in polygon:
                for x,y in ring:
                    native_col=(x-transform[0])/transform[1];native_row=(y-transform[3])/transform[5];assert col-1e-8<=native_col<=col+1+1e-8 and row-1e-8<=native_row<=row+1+1e-8
        assert v['artifactSha256'] and v['sessions'] and v['evidenceClass']
    valid=[p for p in profile['samples'] if p['value'] is not None];missing=[p for p in profile['samples'] if p['value'] is None]
    for p in valid:assert p['value']==float(data[p['row'],p['column']]) and p['tileSha256'] and p['sessions']
    assert len(values)==576 and len(valid)==143 and len(missing)==114 and maximum<1e-12
    with (browser/'genuine-terrain-profile-analysis.csv').open(newline='') as f:rows=list(csv.DictReader(f));assert len(rows)==513
    for row in rows:
        if row['Original elevation']:assert float(row['Original elevation'])==float(data[int(row['Native row']),int(row['Native column'])]) and row['Source manifest SHA256']==source['manifestSha256']
    return dict(reference='GDAL '+gdal.VersionInfo('RELEASE_NAME'),nativeCells=1024,apiNativeCells=len(values),profileValid=len(valid),profileGaps=len(missing),csvSamples=len(rows),maximumCenterDifferenceDegrees=maximum,originalSha256=hashlib.sha256(original.read_bytes()).hexdigest(),datum='Unknown, unchanged; no MSL/HAG inference')

def lidar(root,original,browser):
    import laspy, pyproj, numpy as np
    probe=read(root/'probe.json');data=laspy.read(original);sha=hashlib.sha256(original.read_bytes()).hexdigest();assert sha==probe['parentOriginalSha256'];values=read(browser/'genuine-lidar-api-values.json');transform=pyproj.Transformer.from_crs(3857,4326,always_xy=True);maximum=0
    for v in values:
        p=v['point']['original'];i=p['record'];assert v['id']==sha+':'+str(i) and v['sourceId']==probe['sourceId'];assert [p['rawX'],p['rawY'],p['rawZ']]==[int(data.X[i]),int(data.Y[i]),int(data.Z[i])]
        assert [p['x'],p['y'],p['z']]==[float(data.x[i]),float(data.y[i]),float(data.z[i])];assert p['classification']==int(data.classification[i]) and p['intensity']==int(data.intensity[i]) and p['returnNumber']==int(data.return_number[i]) and p['numberOfReturns']==int(data.number_of_returns[i])
        if p.get('rawGpsTimeHex'):assert p['rawGpsTimeHex']==format(int(np.asarray(data.gps_time).view(np.uint64)[i]),'016x')
        x,y=transform.transform(float(data.x[i]),float(data.y[i]));maximum=max(maximum,abs(x-v['position']['longitude']),abs(y-v['position']['latitude']));assert v['point']['support'] and v['sessions'] and v['artifactSha256']
    assert len(values)>10000 and len(values)<=32768 and maximum<1e-10
    with (browser/'genuine-lidar-loaded-analysis.csv').open(newline='') as f:rows=list(csv.DictReader(f))
    assert len(rows)==len(values)
    for row in rows:
        p=json.loads(row['Original point attributes (GPS hex preserves exact bits)']);i=p['record'];assert int(p['rawX'])==int(data.X[i]) and int(p['rawY'])==int(data.Y[i]) and int(p['rawZ'])==int(data.Z[i]);assert float(row['Original value'])==float(data.z[i]);assert row['Source ID']==probe['sourceId']
    profile=read(root/'profile.json');geod=pyproj.Geod(ellps='WGS84');aircraft={p['ordinal']:p for p in profile['aircraft']};distance_error=0
    for p in profile['samples']:
        start=aircraft[p['fromPose']];_,_,d=geod.inv(start['position']['longitude'],start['position']['latitude'],p['position']['longitude'],p['position']['latitude']);distance_error=max(distance_error,abs(start['distanceMetres']+d-p['distanceMetres']))
    assert distance_error<1e-6
    origin=read(browser/'browser-validation.json')['performance']['renderOrigin'];first=values[0]['point']['original'];assert [origin['x'],origin['y'],origin['z']]==[first['x'],first['y'],first['z']]
    return dict(reference='laspy '+laspy.__version__+' / PROJ '+pyproj.proj_version_str,nativePointRecords=len(data),loadedApiRecords=len(values),csvRecords=len(rows),maximumGeographicDifferenceDegrees=maximum,maximumGroundDistanceErrorMetres=distance_error,originalSha256=sha,sourceDatumAndZUnits='Unknown, unchanged; illustrative aspect only')

if __name__=='__main__':
    p=argparse.ArgumentParser();p.add_argument('mode',choices=['terrain','lidar']);p.add_argument('probe',type=pathlib.Path);p.add_argument('original',type=pathlib.Path);p.add_argument('browser',type=pathlib.Path);p.add_argument('receipt',type=pathlib.Path);args=p.parse_args();marker=read(args.probe/'probe.json');assert marker['syntheticFlight'] and marker['genuineHistoricalSources'] and marker['networkDownloads']==0 and marker['originalScienceUnchanged'];assert not args.receipt.exists(),'Preserve earlier receipts'
    result=(terrain if args.mode=='terrain' else lidar)(args.probe,args.original,args.browser);receipt=dict(state='Passed',utc=datetime.datetime.now(datetime.timezone.utc).isoformat(),scope='Genuine historical originals; synthetic archived flight and browser analysis, not MSFS acceptance',**result);args.receipt.write_text(json.dumps(receipt,indent=2)+'\n');print(json.dumps(receipt))
