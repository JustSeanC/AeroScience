"""Provider release evidence validation; tests do not perform or stand in for online acceptance."""
import copy
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
import sys
from unittest.mock import patch

ROOT=Path(__file__).resolve().parents[1]
spec=importlib.util.spec_from_file_location('science_acceptance',ROOT/'scripts/release/verify_scientific_acceptance.py')
science=importlib.util.module_from_spec(spec);spec.loader.exec_module(science)
sys.path.insert(0,str(ROOT/'scripts/release'))
import scientific_evidence as evidence
class ScientificAcceptanceTests(unittest.TestCase):
    def provenance_fixture(self,value):
        # Temporary validator input only: no run, network access or canonical write.
        value['version']=science.VERSION
        value['runProvenance']=[{'runId':'synthetic-validator-only','status':'passed','sourceCommit':'f'*40,'receiptSha256':'0'*64,'sourceManifestSha256':science.manifest_digest(value['canonicalLfSourceSha256'])}]
        return value
    def validation_fixture(self):
        # Historical genuine metadata used ONLY as temporary validator-unit input.
        # Updated identities here do not create/publish current network evidence.
        value=json.loads((ROOT/'docs/research/v0.3.1-automatic-provider-acceptance.json').read_text(encoding='utf-8'))
        value['test_only']='Validator fixture; not a current-source acceptance run'
        value['canonicalLfSourceSha256']={n:science.source_digest(ROOT/n) for n in science.SOURCES}
        value['independentComparison']={'results':[{'provider':w['workflow'],'comparisons':1,'valid_derived_values':w['validDerivedSamples'],'source_hashes':[f['sha256'] for f in w.get('sourceFiles',[])]} for w in value['workflows']]}
        return self.provenance_fixture(value)

    def test_current_report_cannot_promote_pending_development_into_release_acceptance(self):
        value=json.loads(science.REPORT.read_text(encoding='utf-8'))
        if value.get('evidenceType')=='pending-genuine-provider-network':
            with self.assertRaisesRegex(ValueError,'pending'):science.verify()
            self.assertEqual([],value['workflows']);self.assertEqual('not tested',value['windowsMsfs'])
        else:
            accepted=science.verify();self.assertEqual(4,len(accepted['workflows']));self.assertEqual('not tested',accepted['windowsMsfs'])
    def test_pending_final_rerun_blocks_distribution(self):
        value=self.validation_fixture();value['finalNetworkRerun']='pending';value['finalNetworkRerunReason']=None
        with tempfile.TemporaryDirectory() as temp:
            path=Path(temp)/'report.json';path.write_text(json.dumps(value), encoding='utf-8')
            with self.assertRaisesRegex(ValueError,'pending'):science.verify(path)

    def test_frozen_v050_evidence_is_preserved_but_cannot_approve_development_source(self):
        for name,validate in [('automatic-provider',science.verify_providers),('guided-envi',science.verify_guided_envi),('workspace',science.verify_workspace)]:
            path=ROOT/f'docs/research/v0.5.0-{name}-acceptance.json';original=path.read_bytes()
            self.assertEqual('0.5.0',json.loads(original)['version'])
            with self.subTest(name=name),self.assertRaises(ValueError):validate(path)
            self.assertEqual(original,path.read_bytes())

    def test_provider_recorder_validation_is_separate_but_full_release_retains_dependencies(self):
        value=self.validation_fixture()
        with tempfile.TemporaryDirectory() as tmp:
            path=Path(tmp)/'test-only.json';path.write_text(json.dumps(value))
            with patch.object(science,'verify_guided_envi',side_effect=ValueError('required guided evidence pending')):
                self.assertEqual(4,len(science.verify_providers(path)['workflows']))
                with self.assertRaisesRegex(ValueError,'guided evidence pending'):science.verify(path)
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
                path=Path(temp)/'report.json';path.write_text(json.dumps(value), encoding='utf-8')
                with self.subTest(case=case),self.assertRaises(ValueError):science.verify(path,require_current=False)

    def current_identity_fixture(self, name):
        # Historical genuine metadata exercises validator rules in a temporary unit
        # fixture only. It cannot become current release evidence or a network pass.
        value=json.loads((ROOT/'docs/research'/('v0.4.0-'+name+'-acceptance.json')).read_text(encoding='utf-8'))
        value['test_only']='Temporary validator-unit input, not current-source scientific acceptance'
        value['canonicalLfSourceSha256']={n:science.source_digest(ROOT/n) for n in science.SOURCES}
        return self.provenance_fixture(value)

    def test_current_guided_and_workspace_pending_evidence_cannot_approve_release(self):
        for name,validate in [('guided-envi',science.verify_guided_envi),('workspace',science.verify_workspace)]:
            path=ROOT/'docs/research'/('v'+science.VERSION+'-'+name+'-acceptance.json')
            value=json.loads(path.read_text(encoding='utf-8'))
            with self.subTest(name=name):
                if value.get('evidenceType','').startswith('pending-'):
                    with self.assertRaisesRegex(ValueError,'pending'):validate()
                else:validate()

    def test_guided_envi_requires_actual_import_product_offline_and_current_source_evidence(self):
        value=self.current_identity_fixture('guided-envi')
        with tempfile.TemporaryDirectory() as temp:
            path=Path(temp)/'envi.json';path.write_text(json.dumps(value), encoding='utf-8');self.assertGreaterEqual(science.verify_guided_envi(path)['bands'],16)
        for key in ('automaticManifest','originalChannelsPreserved','originalFilesPreserved','offlineReopen','duplicateReused'):
            changed=copy.deepcopy(value);changed['validation'][key]=False
            with tempfile.TemporaryDirectory() as temp:
                path=Path(temp)/'envi.json';path.write_text(json.dumps(changed), encoding='utf-8')
                with self.subTest(key=key),self.assertRaises(ValueError):science.verify_guided_envi(path)

    def test_workspace_requires_current_genuine_numeric_support_and_distinct_mission_packages(self):
        actual=self.current_identity_fixture('workspace')
        with tempfile.TemporaryDirectory() as temp:
            path=Path(temp)/'workspace.json';path.write_text(json.dumps(actual), encoding='utf-8');science.verify_workspace(path)
        for change in ('stale','nodata','regions','packages','hardware'):
            value=copy.deepcopy(actual)
            if change=='stale':value['canonicalLfSourceSha256'][science.SOURCES[0]]='0'*64
            if change=='nodata':value['independentRasterResults'][0]['nodataSupportMatches']=False
            if change=='regions':value['regionalSourcesCombined']=1
            if change=='packages':value['researchPackagesVerified']=0
            if change=='hardware':value['windowsMsfs']='passed'
            with tempfile.TemporaryDirectory() as temp:
                path=Path(temp)/'workspace.json';path.write_text(json.dumps(value), encoding='utf-8')
                with self.subTest(change=change),self.assertRaises(ValueError):science.verify_workspace(path)

    def test_missing_incomplete_wrong_version_or_stale_run_binding_is_refused(self):
        for change in ('missing','incomplete','source','version','format'):
            value=self.current_identity_fixture('guided-envi')
            if change=='missing':value.pop('runProvenance')
            if change=='incomplete':value['runProvenance'][0]['status']='running'
            if change=='source':value['runProvenance'][0]['sourceManifestSha256']='0'*64
            if change=='version':value['version']='0.4.2'
            if change=='format':value['formatVersion']=99
            with tempfile.TemporaryDirectory() as tmp:
                path=Path(tmp)/'test-only.json';path.write_text(json.dumps(value))
                with self.subTest(change=change),self.assertRaises(ValueError):science.verify_guided_envi(path)

    def test_failed_validation_preserves_existing_canonical_bytes(self):
        with tempfile.TemporaryDirectory() as tmp:
            path=Path(tmp)/'record.json';path.write_bytes(b'original pending evidence\n')
            def refuse(_):raise ValueError('synthetic validation refusal')
            with self.assertRaises(ValueError):science.write_verified(path,{'unverified':'input'},refuse)
            self.assertEqual(b'original pending evidence\n',path.read_bytes())
            self.assertEqual([path],list(Path(tmp).iterdir()))

    def test_execution_receipt_refuses_changed_raw_data_source_or_unfinished_work(self):
        with tempfile.TemporaryDirectory() as tmp,patch.object(evidence.subprocess,'check_output',side_effect=lambda args,**kw: '' if args[1]=='status' else 'f'*40):
            folder=Path(tmp)/'synthetic-run';snapshot=evidence.begin(folder,'providers');folder.mkdir()
            artifact=folder/'raw.json';artifact.write_text('{"synthetic_test_only":true}')
            with self.assertRaises(ValueError):evidence.load_run(folder,'providers')
            evidence.seal(folder,snapshot);run,_=evidence.load_run(folder,'providers')
            self.assertEqual('passed',run['status'])
            with patch.object(evidence.verification,'source_hashes',return_value={'changed':'source'}):
                with self.assertRaisesRegex(ValueError,'restamp'):evidence.load_run(folder,'providers')
            artifact.write_text('changed synthetic fixture')
            with self.assertRaisesRegex(ValueError,'artifact changed'):evidence.load_run(folder,'providers')

    def test_evidence_paths_and_signed_urls_cannot_escape_into_public_records(self):
        with tempfile.TemporaryDirectory() as tmp:
            for relative in ('../private.txt','C:/private/token.txt'):
                with self.subTest(relative=relative),self.assertRaises(ValueError):evidence.safe_file(Path(tmp),relative)
        for url in ('https://example.invalid/data?sig=secret','https://example.invalid/data?access_token=secret','https://example.invalid/data?X-Amz-Credential=secret'):
            with self.subTest(url=url),self.assertRaises(ValueError):evidence.refuse_secrets({'reference':url})
