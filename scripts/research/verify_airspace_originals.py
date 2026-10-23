"""Read-only independent FAA original airspace comparison (pyshp/Shapely, not NTS)."""
import argparse
import hashlib
import json
from pathlib import Path
import sqlite3
import struct
import uuid
import zipfile
import xml.etree.ElementTree as ET

import shapefile
from shapely import from_wkb, normalize, equals_exact
from shapely.geometry import shape, Polygon, MultiPolygon, box

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


def saa_geometry(document):
    airspaces = document.findall('.//a:Airspace', NS)
    if len(airspaces) != 1:
        return None
    components = airspaces[0].findall('a:timeSlice/a:AirspaceTimeSlice/a:geometryComponent/a:AirspaceGeometryComponent', NS)
    if len(components) != 1 or components[0].findtext('a:operation', namespaces=NS) != 'BASE':
        return None
    volumes = components[0].findall('a:theAirspaceVolume/a:AirspaceVolume', NS)
    if len(volumes) != 1:
        return None
    surface = volumes[0].find('a:horizontalProjection/a:Surface', NS)
    assert surface is not None and surface.attrib['srsName'].lower() == 'urn:ogc:def:crs:ogc:1.3:crs84'
    if surface.findall('.//g:ArcByCenterPoint', NS) or surface.findall('.//g:CircleByCenterPoint', NS):
        return None
    polygons = []
    for patch in surface.findall('.//g:PolygonPatch', NS):
        def ring(boundary):
            points = []
            for element in boundary.iter():
                if element.tag in ('{'+NS['g']+'}pos', '{'+NS['g']+'}posList'):
                    values = list(map(float, element.text.split()))
                    points.extend(zip(values[::2], values[1::2]))
            return points
        polygons.append(Polygon(ring(patch.find('g:exterior', NS)), [ring(b) for b in patch.findall('g:interior', NS)]))
    return polygons[0] if len(polygons) == 1 else MultiPolygon(polygons)


def verify(root):
    folder = root.resolve() / 'AviationReference'
    results = []
    with sqlite3.connect((folder / 'aviation.db').as_uri()+'?mode=ro', uri=True) as db:
        datasets = [json.loads(row[0]) for row in db.execute('SELECT payload FROM vector_datasets')]
        for dataset in (d for d in datasets if d['family'] in EXPECTED):
            family = dataset['family']
            job_id = str(uuid.UUID(dataset['jobId']))
            job = json.loads(db.execute('SELECT payload FROM download_jobs WHERE id=?', (job_id,)).fetchone()[0])
            original = folder / 'Originals' / uuid.UUID(job_id).hex
            assert dataset['edition'] == '2026-10-01'
            assert digest(original/'original.zip') == dataset['originalSha256'] == job['originalSha256'] == EXPECTED[family]
            for f in job['files']:
                assert Path(f['name']).name == f['name']
                p = original/'files'/f['name']
                assert p.stat().st_size == f['bytes'] and digest(p) == f['sha256']
            retained = {json.loads(payload)['sourceRecord']: (from_wkb(wkb), json.loads(payload)) for wkb, payload in db.execute('SELECT geometry,payload FROM airspace_features WHERE dataset_id=?', (dataset['id'],))}
            region_shapes = {'Massachusetts': box(-73, 41, -70, 43), 'California': box(-124, 32, -114, 42)}
            regions = dict.fromkeys(region_shapes, 0)
            compared, attributes, coordinates, omitted = 0, 0, 0, 0

            def compare(number, geometry, raw):
                nonlocal compared, coordinates, attributes, omitted
                if number not in retained:
                    omitted += 1
                    # A valid unsupported subset is an explicit omission, never an inferred all-clear.
                    return
                actual, payload = retained[number]
                assert geometry is not None and geometry.is_valid
                assert equals_exact(normalize(actual), normalize(geometry), tolerance=0), f'{family} original coordinates differ: {number}'
                assert payload['attributes'] == raw, f'{family} original attributes differ: {number}'
                attributes += len(raw)
                coordinates += sum(len(p.exterior.coords)+sum(len(r.coords) for r in p.interiors) for p in ([geometry] if geometry.geom_type == 'Polygon' else geometry.geoms))*2
                for name, region in region_shapes.items():
                    regions[name] += int(geometry.intersects(region))
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
                            volume = document.find('.//a:AirspaceVolume', NS)
                            for prefix, field in [('LOWER', 'lowerLimit'), ('UPPER', 'upperLimit')]:
                                value = volume.find('a:'+field, NS)
                                raw[prefix+'_VAL'] = (value.text or '') if value is not None else ''
                                raw[prefix+'_UOM'] = value.attrib.get('uom', '') if value is not None else ''
                                raw[prefix+'_CODE'] = volume.findtext('a:'+field+'Reference', default='', namespaces=NS)
                        compare(number, saa_geometry(document), raw if expected else {})
            assert compared == dataset['features'] and omitted == dataset['omittedRecords'] and all(regions.values())
            results.append({'family': family, 'edition': dataset['edition'], 'originalSha256': EXPECTED[family], 'featuresCompared': compared, 'originalCoordinateComponentsCompared': coordinates, 'originalAttributeFieldsChecked': attributes,
                'maximumCoordinateDifference': 0, 'regions': regions, 'explicitUnsupportedRecords': omitted, 'sourceCrs': dataset['sourceCrs']})
    assert {r['family'] for r in results} == set(EXPECTED)
    return {'evidenceClass': 'Genuine FAA originals; independent pyshp/Shapely/ElementTree versus retained NTS geometry. Offline read-only verification.', 'results': results,
        'limitations': ['No live activation, TFR clearance, certified positional accuracy or owner-installed Windows acceptance.', 'SAA curves/composites/contributor relationships remain unsupported and omitted.', 'NAD83 coordinates preserved without claiming a precise WGS84 transformation.']}


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--folder', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    args.output.write_text(json.dumps(verify(args.folder), indent=2)+'\n')
    print('PASS: genuine Class/SAA original geometry and attributes independently compared offline in two regions.')
