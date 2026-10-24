"""Original-byte integrity and evidence classification; never NASA numeric acceptance."""
import hashlib
import json
from pathlib import Path
import unittest

ROOT=Path(__file__).resolve().parents[1]


class EmitProductEvidenceTests(unittest.TestCase):
    def test_every_synthetic_original_matches_its_retained_hash_and_no_file_is_unlisted(self):
        root=ROOT/'tests/AeroScience.EmitFixtures';receipt=json.loads((root/'provenance.json').read_text())
        self.assertIn('NOT NASA original',receipt['evidenceClass'])
        self.assertEqual({p.relative_to(root).as_posix() for p in root.rglob('*.nc')},{f['file'] for f in receipt['files']})
        for original in receipt['files']:
            with self.subTest(original=original['file']):
                path=root/original['file'];self.assertIn('20990101T000000',path.name)
                self.assertEqual(original['bytes'],path.stat().st_size)
                self.assertEqual(original['sha256'],hashlib.sha256(path.read_bytes()).hexdigest())

    def test_official_metadata_ledger_preserves_authentication_and_numeric_acceptance_limits(self):
        ledger=json.loads((ROOT/'docs/research/v0.6.0-emit-product-definitions.json').read_text())
        self.assertEqual({('EMITL2ARFL','002'),('EMITL2AMASK','003')},{(c['shortName'],c['version']) for c in ledger['collections']})
        self.assertEqual('blocked-earthdata-authorization',ledger['originalAccess']['status'])
        self.assertEqual(0,ledger['originalAccess']['protectedOriginalBytesRead'])
        self.assertIn('pending',ledger['originalAccess']['numericalAcceptance'])
        self.assertIn('not assumed interchangeable',ledger['compatibility']['mask'])


if __name__=='__main__':unittest.main()
