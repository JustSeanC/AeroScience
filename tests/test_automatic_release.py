"""Synthetic release-control evidence only; no Windows/hardware claims."""
import copy
import importlib
import json
import unittest
import zipfile
import installer_fixtures as fixtures
import test_release_flow as flow

a = importlib.import_module('automatic_release')

class AutomaticReleaseTests(unittest.TestCase):
    def setUp(self):
        self.flow = flow.ReleaseFlowTests(); self.flow.setUp(); self.addCleanup(self.flow.tearDown)

    def evidence(self):
        root=self.flow.root
        unit=root/'unit.trx';unit.write_text('<TestRun><ResultSummary><Counters total="3" passed="3" failed="0" error="0" aborted="0" notExecuted="0"/></ResultSummary></TestRun>')
        browser=root/'browser.json';browser.write_text(json.dumps(dict(stats=dict(expected=2,unexpected=0,flaky=0,skipped=1))))
        helper=root/'helpers.json';helper.write_text(json.dumps(dict(successful=True,passed=5,failed=0,errors=0,skipped=1)))
        recovery=root/'recovery.json';recovery.write_text('{"result":"passed"}')
        output=root/'technical.json';a.evidence(unit,browser,helper,recovery,flow.COMMIT,output)
        return output

    def test_default_alpha_finalization_needs_no_manual_json_and_is_idempotent(self):
        folder=fixtures.candidate(self.flow);self.flow.acceptance.unlink()
        output=self.flow.root/'final';evidence=self.evidence()
        info=a.finalize(folder,output,evidence);setup=output/a.p.identity()['setup_name'];original=setup.read_bytes()
        self.assertEqual('notVerified',info['hardware_acceptance']);self.assertEqual('notPublished',info['github_publication'])
        self.assertEqual(info,a.finalize(folder,output,evidence));self.assertEqual(original,setup.read_bytes())
        self.assertEqual('finalized',a.status(output,flow.COMMIT)['state'])
        with zipfile.ZipFile(output/a.p.identity()['asset_name']) as archive:
            self.assertFalse(any(n.endswith('ACCEPTANCE.json') for n in archive.namelist()))
            self.assertTrue(any(n.endswith('ALPHA-VALIDATION.json') for n in archive.namelist()))
        self.assertIn('NOT VERIFIED', (output/'RELEASE-NOTES.md').read_text())

    def test_failed_tests_zero_tests_forged_hardware_status_and_wrong_source_are_rejected(self):
        value=a.p.read_json(self.evidence())
        for change in ('failed','zero','hardware','source'):
            bad=copy.deepcopy(value)
            if change=='failed':bad['checks']['dotnet']['failed']=1
            if change=='zero':bad['checks']['browser']['expected']=0
            if change=='hardware':bad['hardware_acceptance']='passed'
            if change=='source':bad['source_commit']='b'*40
            with self.subTest(change=change),self.assertRaises(ValueError):a.validate_evidence(bad,flow.COMMIT)

    def test_status_distinguishes_incomplete_changed_source_and_changed_installer(self):
        root=self.flow.root/'final';root.mkdir();meta=a.p.identity()
        self.assertEqual('empty',a.status(root,flow.COMMIT)['state'])
        (root/'installer-candidate').mkdir();self.assertEqual('incomplete',a.status(root,flow.COMMIT)['state'])
        (root/'installer-candidate').rmdir()
        folder=fixtures.candidate(self.flow);a.finalize(folder,root,self.evidence())
        self.assertEqual('changedSource',a.status(root,'b'*40)['state'])
        with (root/meta['setup_name']).open('ab') as file:file.write(b'changed')
        self.assertEqual('corruptOrConflicting',a.status(root,flow.COMMIT)['state'])

    def test_existing_notes_and_partial_final_files_are_not_overwritten(self):
        folder=fixtures.candidate(self.flow);output=self.flow.root/'final';proof=self.evidence()
        a.finalize(folder,output,proof);(output/'RELEASE-NOTES.md').write_text('wrong notes')
        with self.assertRaisesRegex(ValueError,'notes differ'):a.finalize(folder,output,proof)
        (output/'RELEASE-NOTES.md').unlink()
        self.assertEqual('incomplete',a.status(output,flow.COMMIT)['state'])
        with self.assertRaisesRegex(ValueError,'Incomplete'):a.finalize(folder,output,proof)

    def test_actual_evidence_parser_rejects_failing_or_missing_reports(self):
        root=self.flow.root;self.evidence();(root/'helpers.json').write_text('{"successful":false,"passed":5,"failed":1,"errors":0}')
        with self.assertRaisesRegex(ValueError,'helper tests failed'):a.evidence(root/'unit.trx',root/'browser.json',root/'helpers.json',root/'recovery.json',flow.COMMIT,root/'bad.json')
