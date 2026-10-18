"""Provider release evidence validation; tests do not perform or stand in for online acceptance."""
import copy
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

ROOT=Path(__file__).resolve().parents[1]
spec=importlib.util.spec_from_file_location('science_acceptance',ROOT/'scripts/release/verify_scientific_acceptance.py')
science=importlib.util.module_from_spec(spec);spec.loader.exec_module(science)
class ScientificAcceptanceTests(unittest.TestCase):
    def test_genuine_report_is_complete_and_matches_source(self):
        value=science.verify(require_current=False);self.assertEqual(4,len(value['workflows']));self.assertEqual('not tested',value['windowsMsfs'])
    def test_pending_final_rerun_blocks_distribution(self):
        value=json.loads(science.REPORT.read_text());value['finalNetworkRerun']='pending'
        with tempfile.TemporaryDirectory() as temp:
            path=Path(temp)/'report.json';path.write_text(json.dumps(value))
            with self.assertRaisesRegex(ValueError,'pending'):science.verify(path)
    def test_mock_pass_hardware_claim_missing_numeric_workflow_or_stale_source_is_refused(self):
        actual=json.loads(science.REPORT.read_text())
        for case in ('mock','hardware','missing','samples','source'):
            value=copy.deepcopy(actual)
            if case=='mock':value['evidenceType']='synthetic fixtures'
            if case=='hardware':value['windowsMsfs']='passed'
            if case=='missing':value['workflows'].pop()
            if case=='samples':value['workflows'][0]['validDerivedSamples']=0
            if case=='source':value['canonicalLfSourceSha256'][science.SOURCES[0]]='0'*64
            with tempfile.TemporaryDirectory() as temp:
                path=Path(temp)/'report.json';path.write_text(json.dumps(value))
                with self.subTest(case=case),self.assertRaises(ValueError):science.verify(path,require_current=False)
