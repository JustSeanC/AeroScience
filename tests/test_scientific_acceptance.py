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
    def validation_fixture(self):
        # Historical genuine metadata used ONLY as temporary validator-unit input.
        # Updated identities here do not create/publish current network evidence.
        value=json.loads((ROOT/'docs/research/v0.3.1-automatic-provider-acceptance.json').read_text())
        value['test_only']='Validator fixture; not a current-source acceptance run'
        value['canonicalLfSourceSha256']={n:science.source_digest(ROOT/n) for n in science.SOURCES}
        return value

    def test_current_report_cannot_promote_pending_development_into_release_acceptance(self):
        value=json.loads(science.REPORT.read_text())
        if value.get('evidenceType')=='pending-genuine-provider-network':
            with self.assertRaisesRegex(ValueError,'pending'):science.verify()
            self.assertEqual([],value['workflows']);self.assertEqual('not tested',value['windowsMsfs'])
        else:
            accepted=science.verify();self.assertEqual(4,len(accepted['workflows']));self.assertEqual('not tested',accepted['windowsMsfs'])
    def test_pending_final_rerun_blocks_distribution(self):
        value=self.validation_fixture();value['finalNetworkRerun']='pending';value['finalNetworkRerunReason']=None
        with tempfile.TemporaryDirectory() as temp:
            path=Path(temp)/'report.json';path.write_text(json.dumps(value))
            with self.assertRaisesRegex(ValueError,'pending'):science.verify(path)
    def test_mock_pass_hardware_claim_missing_numeric_workflow_or_stale_source_is_refused(self):
        actual=self.validation_fixture()
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

    def test_guided_envi_requires_actual_import_product_offline_and_current_source_evidence(self):
        value=science.verify_guided_envi();self.assertGreaterEqual(value['bands'],16)
        for key in ('automaticManifest','originalChannelsPreserved','originalFilesPreserved','offlineReopen','duplicateReused'):
            changed=copy.deepcopy(value);changed['validation'][key]=False
            with tempfile.TemporaryDirectory() as temp:
                path=Path(temp)/'envi.json';path.write_text(json.dumps(changed))
                with self.subTest(key=key),self.assertRaises(ValueError):science.verify_guided_envi(path)
