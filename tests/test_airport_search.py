import hashlib,json,unittest
from pathlib import Path
ROOT=Path(__file__).resolve().parents[1]
class AirportSearchTests(unittest.TestCase):
    def test_reviewed_public_domain_snapshot_retains_exact_positions_and_large_coverage(self):
        web=ROOT/'src/AeroScience.Engine/wwwroot';raw=(web/'geography/airport-search.json').read_bytes()
        data=json.loads(raw);proof=json.loads((web/'geography/airport-search-provenance.json').read_text(encoding='utf-8'))
        self.assertEqual(hashlib.sha256(raw).hexdigest(),proof['bundledSha256'])
        self.assertEqual(proof['features'],len(data['airports']));self.assertGreater(len(data['airports']),70000)
        rows={r[0]:r for r in data['airports']}
        self.assertEqual(rows['KJFK'][1:3],['KJFK','JFK']);self.assertIn('Oceano',rows['L52'][3])
        self.assertTrue(all(-90<=r[7]<=90 and -180<=r[8]<=180 and r[9]!='closed' for r in rows.values()))
        self.assertIn('Public domain',proof['license']);self.assertEqual(len(proof['sourceCommit']),40)
    def test_new_airport_files_are_pinned_without_using_a_geocoder(self):
        web=ROOT/'src/AeroScience.Engine/wwwroot';pins=json.loads((web/'assets-manifest.json').read_text(encoding='utf-8'))['files']
        for name in ['geography/airport-search.json','geography/airport-search-provenance.json']:
            self.assertEqual(hashlib.sha256((web/name).read_bytes()).hexdigest(),pins[name])
        self.assertNotIn('nominatim',(web/'location-search.js').read_text(encoding='utf-8').lower())
