"""Validation-tool regressions. Synthetic test records never confer scientific acceptance."""
import copy
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('p08c', ROOT/'scripts/validation/p08c.py')
p = importlib.util.module_from_spec(spec); spec.loader.exec_module(p)


class EvidenceTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.original = self.root/'retained-é.laz'; self.original.write_bytes(b'unit test only')

    def manifest(self):
        return {'schema': 'aeroscience-p08c-inputs-1', 'inputs': [{
            'id': 'test-only', 'path': str(self.original), 'kind': 'laz', 'provider_project': 'SYNTHETIC UNIT TEST',
            'source_date_utc': None, 'horizontal_crs': None, 'vertical_datum': None, 'units': None,
            'native_shape_or_count': 1, 'attributes': [], 'license_access': 'test only',
            'retained_authorization': 'disposable test fixture'}]}

    def test_inventory_preserves_unknowns_and_hashes_original_without_writing_it(self):
        before = self.original.read_bytes()
        item = p.inventory(self.manifest())[0]
        self.assertEqual(before, self.original.read_bytes())
        self.assertEqual(p.digest(self.original), item['sha256'])
        self.assertIsNone(item['source_date_utc']); self.assertIsNone(item['vertical_datum'])

    def test_inventory_refuses_changed_original_missing_metadata_and_unapproved_inputs(self):
        for field, value in [('expected_sha256', '0'*64), ('retained_authorization', ''), ('license_access', ''), ('kind', 'copc')]:
            with self.subTest(field=field):
                manifest = self.manifest(); manifest['inputs'][0][field] = value
                with self.assertRaises(ValueError): p.inventory(manifest)
        manifest = self.manifest(); del manifest['inputs'][0]['vertical_datum']
        with self.assertRaises(ValueError): p.inventory(manifest)

    def test_duplicate_input_ids_are_refused(self):
        manifest = self.manifest(); manifest['inputs'] *= 2
        with self.assertRaises(ValueError): p.inventory(manifest)

    def test_existing_attempt_and_receipt_cannot_be_overwritten(self):
        with self.assertRaises(ValueError): p.fresh(self.root)
        p.write_new(self.root/'receipt.json', {'text': 'Français – ciência'})
        with self.assertRaises(FileExistsError): p.write_new(self.root/'receipt.json', {})
        self.assertEqual('Français – ciência', p.read(self.root/'receipt.json')['text'])

    @unittest.skipIf(sys.platform == 'win32', 'Symlink creation requires a separate Windows privilege')
    def test_evidence_symlinks_are_refused(self):
        link = self.root/'link'; link.symlink_to(self.original)
        with self.assertRaises(ValueError): p.digest(link)

    def test_failed_reference_keeps_diagnostics_and_cannot_be_a_pass(self):
        with self.assertRaises(ValueError):
            p.execute([sys.executable, '-c', 'print("reference failed");raise SystemExit(3)'], self.root, 'oracle')
        self.assertIn('reference failed', (self.root/'oracle.log').read_text())
        p.write_new(self.root/'receipt.json', {'schema': p.SCHEMA, 'outcome': 'Failed'})
        with self.assertRaises(ValueError): p.verify_receipt(self.root/'receipt.json')

    def test_generic_yes_or_old_p08b_event_is_not_numerical_evidence(self):
        for record in [{'notes': 'yes', 'outcome': 'Passed'}, {'format_version': 1, 'test_id': 'dem', 'outcome': 'Passed'}]:
            path = self.root/('case-'+str(len(list(self.root.glob('case-*'))))+'.json')
            p.write_new(path, record)
            with self.assertRaises(ValueError): p.verify_receipt(path)

    def test_json_size_limit_and_nonfinite_receipts_fail_closed(self):
        large = self.root/'large.json'
        with large.open('wb') as f: f.truncate(16*1024**2+1)
        with self.assertRaises(ValueError): p.read(large)
        with self.assertRaises(ValueError): p.write_new(self.root/'nan.json', {'metric': float('nan')})

    def test_protocol_limits_and_old_acceptance_schema_are_unchanged(self):
        sys.path.insert(0, str(ROOT/'scripts/release'))
        import private_validation_080 as legacy
        self.assertEqual(26, len(legacy.TESTS))
        self.assertNotIn('independent-numerical', legacy.TESTS)

    def test_ambiguous_or_nonfinite_json_evidence_is_refused(self):
        for index, text in enumerate(['{"outcome":"Passed","outcome":"Failed"}', '{"error":NaN}']):
            path = self.root/str(index); path.write_text(text)
            with self.assertRaises(ValueError): p.read(path)

    def test_receipt_rechecks_actual_plan_reference_and_artifact_bytes(self):
        probe = self.root/'probe'; probe.mkdir()
        plan = {'schema': p.SCHEMA, 'operation': 'terrain', 'evidence_kind': 'independent-numerical',
                'runtime': {'source_commit': '1'*40, 'build_files_sha256': {'test.dll': '2'*64}},
                'historical_sources': True, 'inputs': self.manifest()['inputs'], 'tolerances': p.TOLERANCES['terrain']}
        reference = {'state': 'Passed', 'scope': 'UNIT TEST ONLY'}
        p.write_new(self.root/'plan.json', plan); p.write_new(probe/'independent-comparison.json', reference)
        record = {**plan, 'outcome': 'Passed', 'independent_reference': reference,
                  'artifacts': p.artifact_records(self.root)}
        p.write_new(self.root/'receipt.json', record)
        p.verify_receipt(self.root/'receipt.json', '1'*40)
        with self.assertRaises(ValueError): p.verify_receipt(self.root/'receipt.json', '3'*40)
        self.original.write_bytes(b'changed artifact')
        with self.assertRaises(ValueError): p.verify_receipt(self.root/'receipt.json', '1'*40)

    @unittest.skipUnless((ROOT/'tests/AeroScience.TestHost/bin/Release/net10.0/AeroScience.TestHost.dll').exists(), 'Build existing TestHost for actual probe refusal checks')
    def test_actual_retained_probe_refuses_changed_source_and_existing_output(self):
        source = self.root/'originals'; source.mkdir(); (source/'sample.tif').write_bytes(b'SYNTHETIC not a TIFF')
        descriptor = {'Id': '11111111-1111-1111-1111-111111111111', 'Manifest': {'Bands': [{'Id':'elevation','File':'sample.tif','Format':'geotiff'}]},
                      'Files':[{'RelativePath':'sample.tif','Bytes':20,'Sha256':'0'*64}], 'Grids':{}}
        (source/'source.json').write_text(json.dumps(descriptor))
        dotnet = os.environ.get('DOTNET','dotnet'); host=ROOT/'tests/AeroScience.TestHost/bin/Release/net10.0/AeroScience.TestHost.dll'
        result = subprocess.run([dotnet,str(host),'--retained-raster-probe',str(source),str(self.root/'new')],capture_output=True,text=True)
        self.assertNotEqual(0,result.returncode)
        self.assertIn('Original',result.stderr)
        self.assertEqual(b'SYNTHETIC not a TIFF',(source/'sample.tif').read_bytes())
        self.assertFalse((self.root/'new').exists())
        result = subprocess.run([dotnet,str(host),'--retained-raster-probe',str(source),str(self.root)],capture_output=True,text=True)
        self.assertNotEqual(0,result.returncode); self.assertIn('fresh output',result.stderr)


if __name__ == '__main__':
    unittest.main()
