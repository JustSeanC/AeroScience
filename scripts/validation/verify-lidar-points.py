"""Independent offline oracle: laspy/lazrs, PROJ and GEOS; never production runtime.

Usage: python verify-lidar-points.py <new isolated LidarProbe output>
Requires pinned development laspy 2.6.1, lazrs 0.7.0, pyproj 3.7.2, shapely 2.1.2.
No network calls, source rewrites or datum/height conversions.
"""
import base64,datetime,hashlib,json,pathlib,sys
import laspy,numpy as np,pyproj,shapely
from shapely.geometry import shape,Point
from shapely.ops import unary_union

def canonical(value):
    if isinstance(value,list):return [canonical(v) for v in value]
    if isinstance(value,dict):return {k[:1].lower()+k[1:]:canonical(v) for k,v in value.items()}
    return value

def verify(root):
    root=root.resolve();receipt=root/'independent-comparison.json'
    if receipt.exists():raise ValueError('Preserve earlier comparison receipts; choose another isolated output')
    probe=canonical(json.loads((root/'probe.json').read_text()));sources=canonical(json.loads((root/'sources.json').read_text()));geometry=json.loads((root/'geometry.json').read_text());chunks=[r['chunk'] for r in geometry if r.get('chunk')]
    assert probe['syntheticFlight'] and probe['genuineHistoricalSources']
    source_root=root/probe['sourceLibrary'];actual={};tiles=list((root/'tiles').glob('*.json'))
    for path in tiles:
        assert path.stat().st_size<=16777216
        tile=json.loads(path.read_text());assert not tile['grantsOriginalScienceCredit'] and tile['algorithm']==probe['algorithm']
        assert tile['geometrySha256']==probe['sha256'] and tile['geometryInputFingerprint']==probe['inputIdentity']
        for point in tile['points']:
            key=(tile['source']['id'],point['original']['record']);assert key not in actual,('duplicate original record',key);actual[key]=point
    polygons=[(c,shape(c['geometry'])) for c in chunks];union=unary_union([p for _,p in polygons]);seen=set();scanned=0;max_error=0;source_receipts=[]
    for source in sources:
        cloud=source['manifest']['pointCloud'];original=cloud['originals'][0];header=original['header'];file=next(f for f in source['files'] if f['relativePath']==original['file']);path=source_root/source['relativeDirectory']/file['relativePath']
        assert path.stat().st_size==file['bytes'] and hashlib.sha256(path.read_bytes()).hexdigest()==file['sha256']
        transform=pyproj.Transformer.from_crs(header['horizontalCrs'],'EPSG:4326',always_xy=True);retained=0
        with laspy.open(path,laz_backend=laspy.LazBackend.LazrsParallel) as reader:
            assert reader.header.point_count==header['pointCount'];assert np.array_equal(reader.header.scales,np.array(header['scale'])) and np.array_equal(reader.header.offsets,np.array(header['offset']))
            names=set(reader.header.point_format.dimension_names);fmt=reader.header.point_format.id;first=0
            for batch in reader.chunk_iterator(8192):
                scanned+=len(batch);assert scanned<=2000000,'Independent acceptance cap: 2 million original candidates'
                x=np.asarray(batch.x);y=np.asarray(batch.y);z=np.asarray(batch.z);lon,lat=transform.transform(x,y);mask=shapely.covers(union,shapely.points(lon,lat));indices=np.flatnonzero(mask)
                for i in indices:
                    key=(source['id'],first+int(i));seen.add(key);assert key in actual,('supported original record missing',key);p=actual[key];o=p['original'];retained+=1
                    def v(name):return np.asarray(getattr(batch,name))[i].item()
                    assert (o['rawX'],o['rawY'],o['rawZ'])==(v('X'),v('Y'),v('Z'))
                    assert (o['x'],o['y'],o['z'])==(float(x[i]),float(y[i]),float(z[i]))
                    assert o['intensity']==v('intensity') and o['returnNumber']==v('return_number') and o['numberOfReturns']==v('number_of_returns') and o['classification']==v('classification')
                    flags=v('synthetic')|(v('key_point')<<1)|(v('withheld')<<2)|((v('overlap')<<3) if 'overlap' in names else 0)
                    assert o['classificationFlags']==flags and o['scanDirectionFlag']==v('scan_direction_flag') and o['edgeOfFlightLine']==v('edge_of_flight_line') and o['pointSourceId']==v('point_source_id') and o['userData']==v('user_data')
                    angle=v('scan_angle') if fmt>=6 else v('scan_angle_rank');assert o['rawScanAngle']==angle and abs(o['scanAngleDegrees']-(angle*.006 if fmt>=6 else angle))<1e-12
                    if 'gps_time' in names:
                        bits=np.asarray(batch.gps_time).view(np.uint64)[i].item();assert o['rawGpsTimeHex']==format(bits,'016x')
                    else:assert o['gpsTime'] is None and o['rawGpsTimeHex'] is None
                    if 'red' in names:assert o['rgb']==[v('red'),v('green'),v('blue')]
                    else:assert o['rgb'] is None
                    if 'nir' in names:assert o['infrared']==v('nir')
                    base=[20,28,26,34,57,63,30,36,38,59,67][fmt];assert base64.b64decode(o['originalExtraBytes'])==batch.array[int(i)].tobytes()[base:]
                    error=max(abs(p['geographic']['longitude']-float(lon[i])),abs(p['geographic']['latitude']-float(lat[i])));max_error=max(max_error,error);assert error<1e-10
                    expected={(c['sessionId'],c['configuration']['id'],c['geometryProfile']['profile']['id'],c['geometryProfile']['profile']['version'],c['index']) for c,polygon in polygons if polygon.covers(Point(lon[i],lat[i]))}
                    bindings={(s['sessionId'],s['configurationId'],s['profileId'],s['profileVersion'],chunk) for s in p['support'] for chunk in s['geometryChunks']};assert expected==bindings
                first+=len(batch)
        source_receipts.append({'id':source['id'],'project':source['manifest']['sceneId'],'fileSha256':file['sha256'],'originalPointCount':header['pointCount'],'retainedPoints':retained,'horizontalCrs':header['horizontalCrs'],'verticalReference':header['verticalReference'],'verticalUnit':header['verticalUnit'],'acquisitionUtc':source['manifest']['observationUtc']})
    assert seen==set(actual),('points outside actual modeled corridor',len(set(actual)-seen));assert len(actual)>0
    assert probe['originalScienceObservations']==probe['originalScienceProducts']==0
    result={'state':'Passed','verifiedUtc':datetime.datetime.now(datetime.timezone.utc).isoformat(),'reference':{'laspy':laspy.__version__,'lazrs':'0.7.0','pyproj':pyproj.__version__,'proj':pyproj.proj_version_str,'shapely':shapely.__version__,'geos':shapely.geos_version_string},'scope':'Genuine historical source points; synthetic archived flight geometry; independent original record/attribute/PROJ/GEOS corridor comparison','scannedOriginalPoints':scanned,'retainedOriginalPoints':len(actual),'maximumGeographicDifferenceDegrees':max_error,'sources':source_receipts,'approvedNetwork':probe.get('network'),'unknowns':['Original survey date and vertical datum/unit where not established by actual retained metadata','Original contractor coordinates before Entwine reprojection'],'notTested':['Installed Windows/MSFS','Native COPC spatial index','Provider-wide coverage or density'],'originalScienceObservations':0,'originalScienceProducts':0}
    receipt.write_text(json.dumps(result,indent=2)+'\n');print(json.dumps({k:result[k] for k in ['state','scannedOriginalPoints','retainedOriginalPoints','maximumGeographicDifferenceDegrees']}))
if __name__=='__main__':verify(pathlib.Path(sys.argv[1]))
