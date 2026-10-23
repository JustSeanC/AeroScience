"""Independent FAA graphical-boundary/notice comparison; never a complete NOTAM clearance check."""
import argparse
import hashlib
import json
from pathlib import Path
import sqlite3

from rasterio.warp import transform
from shapely.geometry import shape


def verify(root):
    raw = {name: (root/(name+'.json')).read_bytes() for name in ('notices', 'no-shape', 'geometry')}
    with sqlite3.connect((root/'AviationReference'/'aviation.db').resolve().as_uri()+'?mode=ro', uri=True) as db:
        snapshot = json.loads(db.execute("SELECT payload FROM snapshots WHERE kind='tfr-geometry'").fetchone()[0])
    for name, field in [('notices', 'noticeSha256'), ('no-shape', 'noShapeSha256'), ('geometry', 'geometrySha256')]:
        assert hashlib.sha256(raw[name]).hexdigest() == snapshot[field], 'Actual original metadata changed.'
    notices = {n['notam_id']: n for n in json.loads(raw['notices'])}
    source = json.loads(raw['geometry'])
    features = {f['properties']['GID']: f for f in source['features']}
    assert len(features) == source['numberReturned'] == source['totalFeatures']
    maximum = 0
    components = 0
    for b in snapshot['boundaries']:
        original = features[b['sourceGeometryId']]
        p = original['properties']
        notice = notices[b['notamId']]
        assert b['sourceFeatureId'] == original['id'] and b['notamKey'] == p['NOTAM_KEY']
        assert b['notamId'] == p['NOTAM_KEY'].split('-')[0]
        for current, original_key, notice_key in [('title','TITLE','description'), ('facility','CNS_LOCATION_ID','facility'), ('state','STATE','state'), ('type','LEGAL','type')]:
            assert b[current] == p[original_key] == notice[notice_key], 'Notice join or source attribution differs.'
        assert b['modificationText'] == p['LAST_MODIFICATION_DATETIME']
        geometry = original['geometry']
        polygons = [geometry['coordinates']] if geometry['type'] == 'Polygon' else geometry['coordinates']
        assert len(polygons) == len(b['polygons'])
        transformed = []
        for polygon, stored in zip(polygons, b['polygons'], strict=True):
            rings = []
            for ring, actual in zip(polygon, stored, strict=True):
                lon, lat = transform('EPSG:3857','EPSG:4326', [c[0] for c in ring], [c[1] for c in ring])
                assert len(lon) == len(actual)
                for x, y, c in zip(lon, lat, actual, strict=True):
                    maximum = max(maximum, abs(x-c['longitude']), abs(y-c['latitude']))
                    components += 2
                rings.append(list(zip(lon,lat)))
            transformed.append(rings)
        assert shape({'type':'MultiPolygon','coordinates':transformed}).is_valid
        assert 'Unknown' in b['timeStatus'] and 'Unknown' in b['altitudeStatus'] and 'unverified' in b['segmentStatus']
    assert maximum < 1e-10 and len(snapshot['boundaries']) > 0
    assert {n['notamId'] for n in snapshot['noticeInventory']} == set(notices)
    missing = set(notices)-{b['notamId'] for b in snapshot['boundaries']}
    return {'evidenceClass':'Genuine FAA graphical-service originals + independent rasterio/PROJ coordinates, Shapely validity and exact official notice joins. No complete NOTAM/segment/altitude/time or owner-device acceptance.',
        'retrievedUtc':snapshot['retrievedUtc'], 'sourceHashes':{name:hashlib.sha256(value).hexdigest() for name,value in raw.items()},
        'publishedGeometryFeatures':len(features), 'joinedBoundaryFeatures':len(snapshot['boundaries']), 'omittedFeatures':snapshot['omittedFeatures'],
        'noticeInventory':len(notices), 'noticesWithoutJoinedBoundary':len(missing), 'coordinateComponentsCompared':components, 'maximumDifferenceDegrees':maximum,
        'snapshotComplete':False, 'limitations':snapshot['limitations']}


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--folder',type=Path,required=True)
    parser.add_argument('--output',type=Path,required=True)
    args = parser.parse_args()
    args.output.write_text(json.dumps(verify(args.folder),indent=2)+'\n')
    print('PASS: original FAA geometry/notice joins and independent geographic transformations. Operational completeness remains unverified.')
