"""Independent original NASR CSV/hash/reference-coordinate comparison; no application writes."""
import argparse
import csv
from collections import defaultdict
import hashlib
import json
from pathlib import Path
import sqlite3
import uuid

EXPECTED = {
    'APT': 'aba48ea877f249d9b8cd63988b14b3432a6a09ad1fbe9561a5e6443c99c03dd0',
    'FIX': '81df483fb8269260aa2060c94e675de7439df8f6664f2479e5f9844d14fc2701',
    'NAV': 'c169cba36c91ff1f89e914c1ab7f3aa09a3149867b8dc53c2f307920a417682e',
}


def digest(path):
    if path.is_symlink():
        raise ValueError('Original references cannot follow links.')
    h = hashlib.sha256()
    with path.open('rb') as stream:
        while block := stream.read(65536):
            h.update(block)
    return h.hexdigest()


def verify(root):
    root = root.resolve()
    database = root / 'AviationReference' / 'aviation.db'
    with sqlite3.connect(database.as_uri() + '?mode=ro', uri=True) as db:
        datasets = [json.loads(row[0]) for row in db.execute('SELECT payload FROM vector_datasets')]
        if {d['family'] for d in datasets} != set(EXPECTED) or len(datasets) != 3:
            raise ValueError('Need exactly the reviewed genuine APT, NAV and FIX datasets.')
        results = []
        for dataset in datasets:
            family = dataset['family']
            job_id = str(uuid.UUID(dataset['jobId']))
            job = json.loads(db.execute('SELECT payload FROM download_jobs WHERE id=?', (job_id,)).fetchone()[0])
            folder = root / 'AviationReference' / 'Originals' / uuid.UUID(job_id).hex
            if dataset['edition'] != '2026-10-01' or job['originalSha256'] != EXPECTED[family] or digest(folder / 'original.zip') != EXPECTED[family]:
                raise ValueError('Genuine reviewed original identity differs; do not restamp the evidence.')
            for entry in job['files']:
                if Path(entry['name']).name != entry['name']:
                    raise ValueError('Unsafe retained original path.')
                file = folder / 'files' / entry['name']
                if file.stat().st_size != entry['bytes'] or digest(file) != entry['sha256']:
                    raise ValueError('Original companion changed.')

            def rows(name):
                with (folder / 'files' / name).open(encoding='utf-8-sig', newline='') as stream:
                    return [{k: v.strip() for k, v in row.items()} for row in csv.DictReader(stream)]

            def coordinates(row):
                return [float(row['LAT_DECIMAL']), float(row['LONG_DECIMAL'])]

            def apt_key(row):
                return row['ARPT_ID'], row['SITE_TYPE_CODE'], row['STATE_CODE']

            primary = rows(family + '_BASE.csv')
            if family == 'APT':
                index = {apt_key(r): r for r in primary if r['COUNTRY_CODE'] == 'US'}
                ends = rows('APT_RWY_END.csv')
                end_index = {(apt_key(r), r['RWY_ID'], r['RWY_END_ID']): r for r in ends if r['COUNTRY_CODE'] == 'US'}
                runways = {(apt_key(r), r['RWY_ID']): r for r in rows('APT_RWY.csv') if r['COUNTRY_CODE'] == 'US'}
                runway_ends = defaultdict(list)
                for (key, runway, _), row in end_index.items():
                    if row['LAT_DECIMAL'] and row['LONG_DECIMAL']:
                        runway_ends[key, runway].append(row)
            elif family == 'NAV':
                index = {(r['NAV_ID'], r['NAV_TYPE'], r['STATE_CODE'], r['CITY']): r for r in primary if r['COUNTRY_CODE'] == 'US'}
            else:
                index = {(r['FIX_ID'], r['ICAO_REGION_CODE'], r['STATE_CODE']): r for r in primary if r['COUNTRY_CODE'] == 'US'}
            count = 0
            coordinate_comparisons = 0
            attribute_comparisons = 0
            region_counts = {'Massachusetts': 0, 'California': 0}
            for payload, in db.execute('SELECT payload FROM vector_features WHERE dataset_id=?', (dataset['id'],)):
                feature = json.loads(payload)
                attrs = feature['attributes']
                kind = feature['kind']
                if family == 'APT':
                    key = apt_key(attrs)
                    if kind == 'runway-end':
                        row = end_index[key, attrs['RWY_ID'], attrs['RWY_END_ID']]
                        expected_points = [coordinates(row)]
                    elif kind == 'runway':
                        row = runways[key, attrs['RWY_ID']]
                        matching = runway_ends[key, attrs['RWY_ID']]
                        if len(matching) != 2:
                            raise ValueError('Runway line lacks two authoritative endpoints.')
                        expected_points = [coordinates(r) for r in matching]
                    else:
                        row = index[key]
                        expected_points = [coordinates(row)]
                elif family == 'NAV':
                    row = index[attrs['NAV_ID'], attrs['NAV_TYPE'], attrs['STATE_CODE'], attrs['CITY']]
                    expected_points = [coordinates(row)]
                else:
                    row = index[attrs['FIX_ID'], attrs['ICAO_REGION_CODE'], attrs['STATE_CODE']]
                    expected_points = [coordinates(row)]
                actual_points = [[p['latitude'], p['longitude']] for p in feature['coordinates']]
                if sorted(actual_points) != sorted(expected_points):
                    raise ValueError('Stored reference coordinates differ from original numeric fields.')
                coordinate_comparisons += len(actual_points) * 2
                for key, value in attrs.items():
                    if row[key] != value:
                        raise ValueError('Retained reference attribute differs from original CSV.')
                    attribute_comparisons += 1
                for name, bounds in [('Massachusetts', (-72, 41, -70, 43)), ('California', (-123, 36, -121, 39))]:
                    if any(bounds[0] <= lon <= bounds[2] and bounds[1] <= lat <= bounds[3] for lat, lon in actual_points):
                        region_counts[name] += 1
                count += 1
            if count != dataset['features'] or any(n == 0 for n in region_counts.values()):
                raise ValueError('Reference inventory or two-region validation is incomplete.')
            results.append({'family': family, 'edition': dataset['edition'], 'originalSha256': EXPECTED[family],
                'featuresCompared': count, 'coordinateComponentsCompared': coordinate_comparisons,
                'originalAttributesCompared': attribute_comparisons, 'originalNumericMaximumDifference': 0,
                'regions': region_counts, 'omittedRecords': dataset['omittedRecords'],
                'sourceCrs': dataset['sourceCrs']})
    return {'evidenceClass': 'Genuine original FAA NASR hashes and independent CSV-versus-application coordinates/attributes; not WGS84 accuracy or owner-installed acceptance',
            'results': results, 'scienceObservationsCreated': False,
            'limitations': ['Original NAD83 coordinates retained; no precision WGS84 transformation is claimed.',
                'No Class airspace/SUA polygons, activation or TFR clearance is validated.',
                'Omitted non-US/missing coordinates and incomplete runway geometry remain explicitly counted.']}


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--folder', required=True, type=Path)
    parser.add_argument('--output', required=True, type=Path)
    args = parser.parse_args()
    result = verify(args.folder)
    args.output.write_text(json.dumps(result, indent=2) + '\n')
    print('PASS: every retained FAA reference coordinate/attribute matches the reviewed genuine originals in two distinct regions.')
