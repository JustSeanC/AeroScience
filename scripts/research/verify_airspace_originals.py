"""Read-only independent FAA original airspace comparison (pyshp/Shapely, not NTS)."""
import argparse
import hashlib
import json
import math
from pyproj import Geod
from pathlib import Path
import sqlite3
import struct
import uuid
import zipfile
import xml.etree.ElementTree as ET

import shapefile
from shapely import from_wkb, normalize, equals_exact
from shapely.geometry import shape, Polygon, MultiPolygon, GeometryCollection, box

EXPECTED = {
    'CLASS': '7057b1a982d5dd5df4d92a5101d222854b415c5ad163d464de36d94100e20201',
    'SUA': '5bd6c9221ec61d1a9af22b18fe4d59d9378d2c38859d9c1a3619c26ff00b657b',
}
NS = {'a': 'http://www.aixm.aero/schema/5.0', 'g': 'http://www.opengis.net/gml/3.2'}


def digest(path):
    if path.is_symlink():
        raise ValueError('Do not follow source links.')
    h = hashlib.sha256()
    with path.open('rb') as stream:
        while block := stream.read(65536):
            h.update(block)
    return h.hexdigest()


def dbf_strings(path):
    # Read raw field strings, including numeric sentinels; do not round via float conversion.
    with path.open('rb') as stream:
        header = stream.read(32)
        count, offset, length = struct.unpack_from('<IHH', header, 4)
        encoding = 'ascii' if header[29] == 0 else 'cp1252'
        fields = []
        while stream.tell() < offset - 1:
            field = stream.read(32)
            fields.append((field[:11].rstrip(b'\0').decode('ascii'), field[16]))
        assert stream.read(1) == b'\r'
        for _ in range(count):
            record = stream.read(length)
            row, start = {}, 1
            for name, size in fields:
                row[name] = record[start:start+size].decode(encoding).rstrip(' \0').lstrip(' ')
                start += size
            yield row


ARC3 = 'WGS84 geodesic partial arcs; inherited CRS84 angles from longitude axis, increasing explicit minor span only. 0.5 m nominal chord target and maximum source endpoint join; derived arc endpoints use adjacent original literal vertices. Original literal vertices/center/radius/angles retained; not certified boundary accuracy.'
ARC4 = 'WGS84 geodesic partial arcs; inherited CRS84 angles from longitude axis, increasing explicit minor span only (unwrapped source angles retained). 0.5 m nominal chord target and maximum source endpoint join; literal endpoints take precedence, adjacent derived arcs join at the preceding computed endpoint. Original literal vertices/center/radius/angles retained in immutable XML; not certified boundary accuracy.'
CIRCLE = 'WGS84 geodesic circle; 0.5 m nominal chord target. Original center/radius/units retained in the immutable XML. Not certified boundary accuracy.'
COMPOSITION = 'Derived CRS84 horizontal geometry: BASE then explicit operationSequence UNION/SUBTR with identical verified original vertical limits. NTS polygon Boolean operations, no topology repair or altitude conversion. Original component curves/coordinates and relationships remain authoritative in the retained XML; no live activation inferred.'


def components(document):
    airspaces = document.findall('.//a:Airspace', NS)
    assert len(airspaces) == 1
    return airspaces[0].findall('a:timeSlice/a:AirspaceTimeSlice/a:geometryComponent/a:AirspaceGeometryComponent', NS)


def level(volume, field):
    e=volume.find('a:'+field,NS);raw=(e.text or '') if e is not None else '';unit=e.attrib.get('uom','') if e is not None else '';code=volume.findtext('a:'+field+'Reference',default='',namespaces=NS)
    try:value=0 if raw=='GND' and code=='SFC' else float(raw)
    except ValueError:value=None
    if value is not None and (not math.isfinite(value) or value<0):value=None
    ref={'MSL':'MSL','SFC':'Above surface','STD':'Standard pressure'}.get(code,'Unknown');u=unit if unit in ('FT','M','FL') else None
    return dict(value=value,unit=u,reference=ref,rawValue=raw,rawUnit=unit,rawReference=code,description='',status='Published unlimited' if raw=='UNL' else 'Known' if value is not None and u and ref!='Unknown' else 'Unknown')


def native_model(document):
    cs=components(document)
    if len(cs)<2:return False
    pairs=[(level(c.find('a:theAirspaceVolume/a:AirspaceVolume',NS),'lowerLimit'),level(c.find('a:theAirspaceVolume/a:AirspaceVolume',NS),'upperLimit')) for c in cs]
    return any(a['status']!='Known' or b['status']!='Known' for a,b in pairs) or any(p!=pairs[0] for p in pairs)


def volume_definitions(document):
    result=[]
    for c in source_components(document)['components']:
        result.append(dict(version=1,sourceComponent=c['sourceComponent'],sequence=c['sequence'],operation=c['operation'],volumeId=c['volumeId'],surfaceId=c['surfaceId'],lower=c['lower'],upper=c['upper'],lowerIncluded=None,upperIncluded=None,polygons=None))
    return result


def source_components(document):
    values=[]
    for i,c in enumerate(components(document),1):
        v=c.find('a:theAirspaceVolume/a:AirspaceVolume',NS);surface=v.find('a:horizontalProjection/a:Surface',NS)
        values.append(dict(sourceComponent=i,sequence=int(c.findtext('a:operationSequence',namespaces=NS)),operation=c.findtext('a:operation',namespaces=NS),
            volumeId=v.attrib.get('{'+NS['g']+'}id'),surfaceId=surface.attrib.get('{'+NS['g']+'}id'),sourceCrs=surface.attrib['srsName'],lower=level(v,'lowerLimit'),upper=level(v,'upperLimit')))
    return dict(version=1,components=sorted(values,key=lambda c:c['sequence']))


def saa_geometry(document, adapter=5):
    cs=components(document)
    if len(cs)==1:
        assert cs[0].findtext('a:operation',namespaces=NS)=='BASE'
        return saa_volume(cs[0].find('a:theAirspaceVolume/a:AirspaceVolume',NS),adapter)
    assert adapter in (4,5)
    ordered=sorted(cs,key=lambda c:int(c.findtext('a:operationSequence',namespaces=NS)))
    manifest=source_components(document)['components']
    if adapter==5 and native_model(document):return GeometryCollection([saa_volume(c.find('a:theAirspaceVolume/a:AirspaceVolume',NS),adapter) for c in ordered])
    assert len(set(c['sequence'] for c in manifest))==len(cs) and manifest[0]['operation']=='BASE'
    assert all((c['lower'],c['upper'])==(manifest[0]['lower'],manifest[0]['upper']) for c in manifest)
    result=None
    for c in ordered:
        geometry=saa_volume(c.find('a:theAirspaceVolume/a:AirspaceVolume',NS),adapter)
        operation=c.findtext('a:operation',namespaces=NS)
        if result is None:result=geometry
        elif operation=='UNION':result=result.union(geometry)
        else:assert operation=='SUBTR';result=result.difference(geometry)
        assert result.is_valid and not result.is_empty
    return result


def saa_volume(volume, adapter):
    surface=volume.find('a:horizontalProjection/a:Surface',NS)
    assert surface is not None and surface.attrib['srsName'].lower()=='urn:ogc:def:crs:ogc:1.3:crs84'
    polygons = []
    for patch in surface.findall('.//g:PolygonPatch', NS):
        def ring(boundary):
            circles = boundary.findall('.//g:CircleByCenterPoint', NS)
            if circles:
                assert len(circles) == 1
                circle = circles[0]
                center = list(map(float,circle.findtext('g:pointProperty/g:Point/g:pos',namespaces=NS).split()))
                radius = circle.find('g:radius',NS)
                metres = float(radius.text) * {'NM':1852,'MI':1609.344,'FT':.3048,'M':1}[radius.attrib['uom']]
                count = max(36,math.ceil(math.pi/math.acos(1-.5/metres)))
                geodesic = Geod(ellps='WGS84')
                points = [geodesic.fwd(*center,360*i/count,metres)[:2] for i in range(count)]
                return points+[points[0]]
            if boundary.findall('.//g:ArcByCenterPoint', NS):
                segments = boundary.findall('g:Ring/g:curveMember/g:Curve/g:segments/*', NS)
                points, decoded = [], []
                geodesic = Geod(ellps='WGS84')
                for segment in segments:
                    if segment.tag == '{'+NS['g']+'}ArcByCenterPoint':
                        center = list(map(float, segment.findtext('g:pointProperty/g:Point/g:pos', namespaces=NS).split()))
                        radius = segment.find('g:radius', NS)
                        metres = float(radius.text) * {'NM':1852, 'MI':1609.344, 'FT':.3048, 'M':1}[radius.attrib['uom']]
                        start, end = [float(segment.findtext('g:'+name, namespaces=NS)) for name in ('startAngle', 'endAngle')]
                        if not 0 < end-start <= 180:
                            return None
                        count = max(2, math.ceil(math.radians(end-start)/(2*math.acos(1-.5/metres))))
                        current = [geodesic.fwd(*center, 90-(start+(end-start)*i/count), metres)[:2] for i in range(count+1)]
                    else:
                        current = []
                        for element in segment:
                            values = list(map(float, element.text.split()))
                            current.extend(zip(values[::2], values[1::2]))
                    assert current
                    decoded.append(current)
                for i, segment in enumerate(segments):
                    following, previous = (i+1) % len(segments), (i-1) % len(segments)
                    assert geodesic.inv(*decoded[i][-1], *decoded[following][0])[2] <= .5
                for i, segment in enumerate(segments):
                    following, previous = (i+1) % len(segments), (i-1) % len(segments)
                    if adapter == 3:
                        if segment.tag.endswith('ArcByCenterPoint'):
                            assert segments[previous].tag.endswith('LineStringSegment') and segments[following].tag.endswith('LineStringSegment')
                            decoded[i][0], decoded[i][-1] = decoded[previous][-1], decoded[following][0]
                    elif segments[following].tag.endswith('ArcByCenterPoint'):
                        decoded[following][0] = decoded[i][-1]
                    elif segment.tag.endswith('ArcByCenterPoint'):
                        decoded[i][-1] = decoded[following][0]
                for current in decoded:
                    for point in current:
                        if not points or point != points[-1]:
                            points.append(point)
                assert points and geodesic.inv(*points[-1], *points[0])[2] <= .5
                if points[-1] != points[0]:
                    points.append(points[0])
                return points
            points = []
            for element in boundary.iter():
                if element.tag in ('{'+NS['g']+'}pos', '{'+NS['g']+'}posList'):
                    values = list(map(float, element.text.split()))
                    points.extend(zip(values[::2], values[1::2]))
            return points
        exterior = ring(patch.find('g:exterior', NS))
        interiors = [ring(b) for b in patch.findall('g:interior', NS)]
        if exterior is None or any(r is None for r in interiors):
            return None
        polygons.append(Polygon(exterior, interiors))
    return polygons[0] if len(polygons) == 1 else MultiPolygon(polygons)


def verify(root, adapter=5):
    folder = root.resolve() / 'AviationReference'
    results = []
    with sqlite3.connect((folder / 'aviation.db').as_uri()+'?mode=ro', uri=True) as db:
        datasets = [json.loads(row[0]) for row in db.execute('SELECT payload FROM vector_datasets')]
        for dataset in (d for d in datasets if d['family'] in EXPECTED):
            family = dataset['family']
            job_id = str(uuid.UUID(dataset['jobId']))
            job = json.loads(db.execute('SELECT payload FROM download_jobs WHERE id=?', (job_id,)).fetchone()[0])
            original = folder / 'Originals' / uuid.UUID(job_id).hex
            expected_id=hashlib.sha256(json.dumps(dict(airspaceParser=adapter if family=='SUA' else 1,id=job['preview']['item']['id'],originalSha256=job['originalSha256']),separators=(',',':')).encode()).hexdigest()
            if dataset['id'] != expected_id:continue # Historical interpretations remain stored; verify only the explicitly selected adapter.
            assert dataset['edition'] == '2026-10-01'
            assert digest(original/'original.zip') == dataset['originalSha256'] == job['originalSha256'] == EXPECTED[family]
            for f in job['files']:
                assert Path(f['name']).name == f['name']
                p = original/'files'/f['name']
                assert p.stat().st_size == f['bytes'] and digest(p) == f['sha256']
            retained = {json.loads(payload)['sourceRecord']: (from_wkb(wkb), json.loads(payload),from_wkb(display) if display else from_wkb(wkb)) for wkb, payload,display in db.execute('SELECT geometry,payload,display FROM airspace_features WHERE dataset_id=?', (dataset['id'],))}
            region_shapes = {'Massachusetts': box(-73, 41, -70, 43), 'California': box(-124, 32, -114, 42)}
            regions = dict.fromkeys(region_shapes, 0)
            compared, attributes, coordinates, omitted = 0, 0, 0, 0
            maximum_difference = 0.0

            def compare(number, geometry, raw):
                nonlocal compared, coordinates, attributes, omitted, maximum_difference
                if number not in retained:
                    omitted += 1
                    # A valid unsupported subset is an explicit omission, never an inferred all-clear.
                    return
                actual, payload, display = retained[number]
                assert geometry is not None and geometry.is_valid
                tolerance = 1e-10 if 'GEOMETRY_INTERPOLATION' in raw or 'GEOMETRY_COMPOSITION' in raw else 0
                difference = actual.hausdorff_distance(geometry) if tolerance else 0
                maximum_difference = max(maximum_difference,difference)
                assert equals_exact(normalize(actual), normalize(geometry), tolerance=tolerance), f'{family} original coordinates differ: {number}'
                actual_attributes=dict(payload['attributes']);expected_attributes=dict(raw)
                if 'SOURCE_COMPONENTS' in expected_attributes:
                    assert json.loads(actual_attributes.pop('SOURCE_COMPONENTS'))==expected_attributes.pop('SOURCE_COMPONENTS'), f'Component identities/roles/limits differ: {number}'
                if payload['version']==2:
                    assert payload['volumes']==volume_definitions(document),f'Native altitude/component definitions differ: {number}'
                    assert len(actual.geoms)==len(geometry.geoms)
                    assert all(equals_exact(normalize(a),normalize(b),tolerance=1e-10) for a,b in zip(actual.geoms,geometry.geoms,strict=True)),f'Native geometry/order differs: {number}'
                    expected_display=None
                    for c,g in zip(source_components(document)['components'],geometry.geoms,strict=True):
                        if c['operation'] in ('BASE','UNION'):expected_display=g if expected_display is None else expected_display.union(g)
                    assert equals_exact(normalize(display),normalize(expected_display),tolerance=1e-10),f'Potential extent differs: {number}'
                assert actual_attributes == expected_attributes, f'{family} original attributes differ: {number}'
                attributes += len(raw)
                def coordinate_count(g):return (len(g.exterior.coords)+sum(len(r.coords) for r in g.interiors))*2 if g.geom_type=='Polygon' else sum(coordinate_count(p) for p in g.geoms)
                coordinates += coordinate_count(geometry)
                for name, region in region_shapes.items():
                    regions[name] += int(display.intersects(region))
                compared += 1

            if family == 'CLASS':
                file = original/'files'/'Class_Airspace.shp'
                with shapefile.Reader(str(file), encoding='cp1252') as reader:
                    for number, (s, raw) in enumerate(zip(reader.iterShapes(), dbf_strings(file.with_suffix('.dbf')), strict=True), 1):
                        compare(number, shape(s.__geo_interface__), raw)
            else:
                with zipfile.ZipFile(original/'files'/'Saa_Sub_File.zip') as archive:
                    for number, name in enumerate(sorted(archive.namelist()), 1):
                        document = ET.fromstring(archive.read(name))
                        expected = retained.get(number)
                        if expected:
                            def text(path):
                                return document.findtext(path, default='', namespaces=NS)
                            def local(local_name):
                                return next((e.text or '' for e in document.iter() if e.tag.rsplit('}', 1)[-1] == local_name), '')
                            usage = document.find('.//a:AirspaceUsageTimeSlice', NS)
                            def usage_text(path):
                                return usage.findtext(path, default='', namespaces=NS) if usage is not None else ''
                            raw = {'ORIGINAL_MEMBER': name, 'NAME': text('.//a:AirspaceTimeSlice/a:name'), 'DESIGNATOR': text('.//a:AirspaceTimeSlice/a:designator'),
                                'SOURCE_IDENTIFIER': text('.//a:Airspace/g:identifier'), 'SUA_TYPE': local('suaType'), 'ADMINISTRATIVE_AREA': local('administrativeArea'),
                                'DEFINITION_BEGIN': text('.//a:AirspaceTimeSlice/g:validTime//g:beginPosition'), 'DEFINITION_END': text('.//a:AirspaceTimeSlice/g:validTime//g:endPosition'),
                                'ACTIVATION_SOURCE_STATUS': usage_text('.//a:statusActivation'), 'WKHR_CODE': usage_text('.//a:workingHours'),
                                'WKHR_RMK': '; '.join(' '.join(e.tag.rsplit('}', 1)[-1]+'='+(e.text or '') for e in timesheet if len(e) == 0) for timesheet in (usage.findall('.//a:Timesheet', NS) if usage is not None else [])),
                                'DST': usage_text('.//a:daylightSavingAdjust'), 'GMTOFFSET': next((e.text or '' for e in usage.iter() if e.tag.rsplit('}', 1)[-1] == 'timeOffset'), '') if usage is not None else ''}
                            cs=components(document)
                            primary=min(cs,key=lambda c:int(c.findtext('a:operationSequence',default='0',namespaces=NS)))
                            volume = primary.find('a:theAirspaceVolume/a:AirspaceVolume',NS)
                            for prefix, field in [('LOWER', 'lowerLimit'), ('UPPER', 'upperLimit')]:
                                value = volume.find('a:'+field, NS)
                                raw[prefix+'_VAL'] = (value.text or '') if value is not None else ''
                                raw[prefix+'_UOM'] = value.attrib.get('uom', '') if value is not None else ''
                                raw[prefix+'_CODE'] = volume.findtext('a:'+field+'Reference', default='', namespaces=NS)
                            if document.findall('.//g:ArcByCenterPoint', NS):
                                raw['GEOMETRY_INTERPOLATION'] = ARC3 if adapter==3 else ARC4
                            elif document.findall('.//g:CircleByCenterPoint',NS):
                                raw['GEOMETRY_INTERPOLATION'] = CIRCLE
                            if len(cs)>1:
                                if adapter==5 and native_model(document):
                                    for key in ('LOWER_VAL','LOWER_UOM','LOWER_CODE','UPPER_VAL','UPPER_UOM','UPPER_CODE'):raw[key]=''
                                    raw.pop('GEOMETRY_INTERPOLATION',None)
                                    raw['GEOMETRY_COMPOSITION']='Ordered native vertical-volume model; map shows potential horizontal extent, not a flattened restriction. Inspect each component and comparable altitude dimensions. Activation remains Unknown.'
                                else:
                                    raw['GEOMETRY_COMPOSITION']=COMPOSITION
                                    raw['SOURCE_COMPONENTS']=source_components(document)
                        compare(number, saa_geometry(document,adapter) if expected else None, raw if expected else {})
            assert compared == dataset['features'] and omitted == dataset['omittedRecords'] and all(regions.values())
            results.append({'family': family, 'edition': dataset['edition'], 'originalSha256': EXPECTED[family], 'featuresCompared': compared, 'originalCoordinateComponentsCompared': coordinates, 'originalAttributeFieldsChecked': attributes,
                'maximumCoordinateDifference': maximum_difference, 'regions': regions, 'explicitUnsupportedRecords': omitted, 'sourceCrs': dataset['sourceCrs']})
    assert {r['family'] for r in results} == set(EXPECTED) and len(results)==2
    return {'evidenceClass': 'Genuine FAA originals; independent pyshp/Shapely/ElementTree versus retained NTS geometry. Offline read-only verification.', 'results': results,
        'limitations': ['No live activation, TFR clearance, certified positional accuracy or owner-installed Windows acceptance.', 'Adapter '+str(adapter)+': explicit positive minor spans, 0.5 m joins, same-original-volume ordered Boolean operations (4+), and separate native unequal/unknown vertical component models with conservative map extents (5). Signed/reversed or inferred wraps, discrepant joins and unresolved contributors remain omitted. Inclusion at exact altitude boundaries and activation remain Unknown. PROJ/GEOS comparisons are not certified boundary accuracy.', 'NAD83 coordinates preserved without claiming a precise WGS84 transformation.']}


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--folder', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--adapter',type=int,choices=[3,4,5],default=5,help='Explicit interpretation version; historical datasets are never silently reinterpreted.')
    args = parser.parse_args()
    args.output.write_text(json.dumps(verify(args.folder,args.adapter), indent=2)+'\n')
    print('PASS: genuine Class/SAA original geometry and attributes independently compared offline in two regions.')
