import hashlib
import json
from pathlib import Path
import unittest
ROOT=Path(__file__).resolve().parents[1]
WEB=ROOT/'src/AeroScience.Engine/wwwroot'
class OfflineAssetsTests(unittest.TestCase):
    def test_committed_bundles_and_license_hashes_match(self):
        manifest=json.loads((WEB/'assets-manifest.json').read_text())
        for name,digest in manifest['files'].items():
            with self.subTest(name=name):self.assertEqual(hashlib.sha256((WEB/name).read_bytes()).hexdigest(),digest)
        for library in ('leaflet','leaflet-draw','chart.js','geographiclib'):
            self.assertGreater((WEB/'vendor'/library/'LICENSE.txt').stat().st_size,500)
    def test_real_geography_is_pinned_public_domain_and_contains_reference_airport(self):
        provenance=json.loads((WEB/'geography/provenance.json').read_text())
        self.assertEqual(provenance['license'],'Public domain')
        for data in provenance['datasets']:
            self.assertIn(provenance['upstream_commit'],data['upstream_url'])
            path=WEB/'geography'/data['file'];self.assertEqual(hashlib.sha256(path.read_bytes()).hexdigest(),data['bundled_sha256'])
            self.assertEqual(len(json.loads(path.read_text())['features']),data['features'])
        airports=json.loads((WEB/'geography/airports.geojson').read_text())['features']
        jfk=next(f for f in airports if f['properties']['gps_code']=='KJFK')
        self.assertAlmostEqual(jfk['geometry']['coordinates'][0],-73.78,delta=.1)
        self.assertAlmostEqual(jfk['geometry']['coordinates'][1],40.64,delta=.1)
