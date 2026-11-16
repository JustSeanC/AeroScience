"""Synthetic beta contract fixtures. Not genuine SDK/installer/installed acceptance."""
import copy
import json
import os
from pathlib import Path
import shutil
import struct
import subprocess
import sys
import tempfile
import unittest
from unittest import mock

ROOT=Path(__file__).resolve().parents[1]
sys.path.insert(0,str(ROOT/'scripts/release'))
import beta_release_080 as beta
import beta_checklist_080 as checklist

SOURCE='a'*40

class Beta080Tests(unittest.TestCase):
    def setUp(self):
        self.temp=tempfile.TemporaryDirectory();self.addCleanup(self.temp.cleanup);self.root=Path(self.temp.name)

    def test_metadata_is_prerelease_while_legacy_identity_and_locks_are_preserved(self):
        self.assertEqual('0.8.0-beta.1',beta.identity()['version']);self.assertEqual('beta',beta.identity()['channel'])
        self.assertEqual('0.7.0',beta.release.identity()['version'])
        self.assertEqual(26,len(beta.native.TESTS))

    def test_fresh_output_refuses_existing_nested_protected_and_linked_paths(self):
        output=self.root/'Beta080-fresh'
        with mock.patch.object(beta.native,'protected_roots',return_value=[]):
            self.assertEqual(output,beta.safe_output(output,repo=self.root/'repo',sdk=self.root/'sdk'))
            output.mkdir()
            with self.assertRaises(ValueError):beta.safe_output(output)
            with self.assertRaises(ValueError):beta.safe_output(self.root/'Validation080-wrong')
            (self.root/'AeroScience.exe').write_bytes(b'SYNTHETIC')
            with self.assertRaises(ValueError):beta.safe_output(self.root/'Beta080-inside-install')

    def test_native_pe_dependencies_are_read_from_actual_import_table(self):
        path=self.root/'native.exe';image=bytearray(2048);image[:2]=b'MZ';struct.pack_into('<I',image,60,128);image[128:132]=b'PE\0\0'
        struct.pack_into('<HH',image,132,0x8664,1);struct.pack_into('<HH',image,148,240,0x22);optional=152
        struct.pack_into('<H',image,optional,0x20b);struct.pack_into('<II',image,optional+120,0x1000,40)
        section=optional+240;struct.pack_into('<IIII',image,section+8,512,0x1000,512,512)
        struct.pack_into('<IIIII',image,512,1,0,0,0x1080,0x1100);image[640:653]=b'KERNEL32.dll\0';path.write_bytes(image)
        self.assertEqual(['kernel32.dll'],beta.imports(path))
        struct.pack_into('<I',image,512+12,0x9000);path.write_bytes(image)
        with self.assertRaises(ValueError):beta.imports(path)

    def test_legacy_release_guard_does_not_admit_the_new_native_helper(self):
        import test_release_flow as flow
        fixture=flow.ReleaseFlowTests();fixture.setUp();self.addCleanup(fixture.tearDown)
        helper=fixture.published/'LidarRuntime/AeroScience.LidarReader.exe';helper.parent.mkdir();helper.write_bytes(b'SYNTHETIC NOT A NATIVE DECODER')
        with self.assertRaises(ValueError):beta.release.validate_windows_payload(fixture.published)
        with self.assertRaises(ValueError):beta.windows_payload(fixture.published)

    def test_native_projection_preserves_bytes_and_still_rejects_unknown_executables_and_owner_data(self):
        import test_release_flow as flow
        fixture=flow.ReleaseFlowTests();fixture.setUp();self.addCleanup(fixture.tearDown)
        helper=fixture.published/'LidarRuntime/AeroScience.LidarReader.exe';helper.parent.mkdir();helper.write_bytes(b'SYNTHETIC helper already checked by separate native guard')
        before=beta.inventory(fixture.published)
        with mock.patch.object(beta,'native_integrity',return_value={}):
            beta.windows_payload(fixture.published)
            self.assertEqual(before,beta.inventory(fixture.published))
            for name in ['surprise.exe','missions.db']:
                bad=fixture.published/name;bad.write_bytes(b'UNEXPECTED SYNTHETIC PAYLOAD')
                with self.assertRaises(ValueError):beta.windows_payload(fixture.published)
                bad.unlink()
        self.assertEqual(before,beta.inventory(fixture.published))

    def test_unsigned_beta_does_not_mean_invalid_signatures_or_unsigned_microsoft_runtime(self):
        text=(ROOT/'scripts/release/Build-080BetaCandidate.ps1').read_text()
        self.assertIn('AllowUnsignedBeta',text);self.assertIn("$signature.Status -ne 'Valid'",text);self.assertIn('Microsoft Corporation',text)
        self.assertNotIn('Invoke-WebRequest',text)
        self.assertFalse((ROOT/'native/lidar/laszip').is_symlink())

    def test_preflight_replaces_only_output_guard_and_keeps_native_sdk_failures(self):
        proof={'checks':[{'name':'output-isolation','outcome':'Passed','evidence':'private shim'},
            {'name':'genuine-SDK-inputs','outcome':'Blocked','next_action':'Actual SDK required'}]}
        with mock.patch.object(beta.native,'preflight',return_value=proof),mock.patch.object(beta.native,'protected_roots',return_value=[]):
            result=beta.preflight(SOURCE,self.root/'sdk',self.root/'approval',self.root/'Beta080-new',True)
        self.assertFalse(result['ready']);self.assertEqual('Blocked',result['checks'][1]['outcome']);self.assertEqual('beta080',result['profile'])

    def test_readiness_keeps_missing_installer_blocked_and_broad_tests_deferred(self):
        result=beta.readiness(self.root,SOURCE)
        self.assertFalse(result['distribution_ready']);self.assertFalse(result['publication_approved'])
        self.assertEqual('Blocked',result['required_pre_beta']['packaging']['outcome'])
        self.assertTrue(all(v['outcome']=='Deferred' for v in result['deferred_beta_testing'].values()))

    def observed(self):
        record=beta.acceptance(SOURCE,'1'*64,'2'*64)
        for row in record['tests'].values():row.update(outcome='Passed',notes='SYNTHETIC control-flow observation; not native acceptance.',personally_observed=True)
        path=self.root/'acceptance.json';path.write_text(json.dumps(record));return path

    def test_exact_basic_acceptance_can_be_ready_without_comprehensive_beta_cards(self):
        path=self.observed();ci=self.root/'ci.json';ci.write_text(json.dumps({'source_commit':SOURCE,'conclusion':'success','scope':'full','passed_jobs':['linux','windows-companion','organization-deployment'],'url':'https://github.com/example/synthetic'}))
        with mock.patch.object(beta,'verify_installer',return_value={'installer_sha256':'1'*64}),mock.patch.object(beta,'sha',return_value='2'*64):
            result=beta.readiness(self.root,SOURCE,path,ci)
        self.assertTrue(result['distribution_ready']);self.assertFalse(result['publication_approved']);self.assertFalse(result['stable_release_approved'])
        self.assertTrue(all(v['outcome']=='Deferred' for v in result['deferred_beta_testing'].values()))

    def test_foreign_installer_or_generic_yes_cannot_inherit_private_acceptance(self):
        path=self.observed();data=json.loads(path.read_text());data['installer_sha256']='3'*64;path.write_text(json.dumps(data))
        with mock.patch.object(beta,'verify_installer',return_value={'installer_sha256':'1'*64}),mock.patch.object(beta,'sha',return_value='2'*64):
            with self.assertRaises(ValueError):beta.readiness(self.root,SOURCE,path)
        data['installer_sha256']='1'*64;data['tests']['toolbar']['notes']='yes';path.write_text(json.dumps(data))
        with mock.patch.object(beta,'verify_installer',return_value={'installer_sha256':'1'*64}),mock.patch.object(beta,'sha',return_value='2'*64):
            with self.assertRaises(ValueError):beta.readiness(self.root,SOURCE,path)

    def test_known_critical_problem_blocks_even_otherwise_passing_beta(self):
        path=self.observed();data=json.loads(path.read_text());data['critical_defects']=['SYNTHETIC corruption report'];path.write_text(json.dumps(data))
        with mock.patch.object(beta,'verify_installer',return_value={'installer_sha256':'1'*64}),mock.patch.object(beta,'sha',return_value='2'*64):
            self.assertFalse(beta.readiness(self.root,SOURCE,path)['distribution_ready'])

    def test_owner_scripts_parse_and_do_not_publish_install_or_kill_existing_apps(self):
        pwsh=shutil.which('pwsh') or '/workspace/.tools/pwsh/pwsh'
        if not Path(pwsh).exists():self.skipTest('Existing PowerShell parser unavailable')
        for name in ['scripts/release/Build-080BetaCandidate.ps1','scripts/build/Publish-080BetaCompanion.ps1','scripts/release/Open-080BetaAcceptance.ps1']:
            command='$e=$null;$t=$null;[void][Management.Automation.Language.Parser]::ParseFile($env:TEST_SCRIPT,[ref]$t,[ref]$e);if($e){throw ($e|Out-String)}'
            result=subprocess.run([pwsh,'-NoProfile','-Command',command],env={**os.environ,'TEST_SCRIPT':str(ROOT/name)},capture_output=True,text=True,timeout=30)
            self.assertEqual(0,result.returncode,result.stderr)
            text=(ROOT/name).read_text();self.assertNotIn('Publish-Release.ps1',text);self.assertNotIn('Stop-Process',text)


class BetaToolbarTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.temp=tempfile.TemporaryDirectory();cls.stage=Path(cls.temp.name)/'stage';beta.stage_toolbar(cls.stage,SOURCE)
        result=subprocess.run(['node',str(ROOT/'scripts/build/native-javascript.mjs'),str(cls.stage)],capture_output=True,text=True,timeout=60,cwd=ROOT)
        if result.returncode:raise RuntimeError(result.stderr)
    @classmethod
    def tearDownClass(cls):cls.temp.cleanup()
    def test_compiled_classic_script_has_exact_beta_fields_without_private_instance(self):
        text=(self.stage/'PackageSources/html_ui/InGamePanels'/beta.PANEL/'AeroScience.js').read_text()
        self.assertIn('betaSource: "'+SOURCE+'"',text);self.assertIn('betaVersion: "0.8.0-beta.1"',text)
        self.assertNotIn('validationSource:',text);self.assertNotIn('validationInstance:',text)
        self.assertIn('AeroScience.Beta.0_8_0.Command.v1',text);self.assertIn('AeroScience.Beta.0_8_0.Reply.v1',text)
        self.assertEqual('ES2017',json.loads((self.stage/'native-javascript.json').read_text())['target'])

class BetaChecklistTests(unittest.TestCase):
    def setUp(self):
        self.temp=tempfile.TemporaryDirectory();self.addCleanup(self.temp.cleanup);self.root=Path(self.temp.name)
        self.p1=mock.patch.object(beta,'verify_installer',return_value={'installer_sha256':'1'*64}).start()
        self.p2=mock.patch.object(beta,'sha',return_value='2'*64).start();self.addCleanup(mock.patch.stopall)
    def entry(self,key='toolbar',notes='Actual observation on SYNTHETIC recorder fixture only.'):
        return {'id':key,'outcome':'Passed','notes':notes,'revision':None}
    def test_required_basic_rows_start_untried_and_do_not_adopt_p08b_or_endurance(self):
        view=checklist.state(self.root,SOURCE,'2'*64);self.assertEqual(8,len(view['rows']));self.assertTrue(all(r['outcome']=='Not Tested' for r in view['rows']))
        with self.assertRaises(ValueError):checklist.save(self.root,SOURCE,'2'*64,{'observed':True,'entries':[self.entry('endurance')]})
    def test_saved_observations_are_append_only_and_bulk_invalid_changes_write_nothing(self):
        with self.assertRaises(ValueError):checklist.save(self.root,SOURCE,'2'*64,{'observed':True,'entries':[self.entry(),self.entry('privacy','yes')]})
        self.assertFalse(list((self.root/'Acceptance-Events').glob('*.json')))
        checklist.save(self.root,SOURCE,'2'*64,{'observed':True,'entries':[self.entry()]})
        self.assertEqual(1,len(list((self.root/'Acceptance-Events').glob('*.json'))))
        with self.assertRaises(ValueError):checklist.save(self.root,SOURCE,'2'*64,{'observed':True,'entries':[self.entry()]})
    def test_installer_pass_needs_actual_verified_installed_files(self):
        with self.assertRaises(ValueError):checklist.save(self.root,SOURCE,'2'*64,{'observed':True,'entries':[self.entry('installer')]})
    def test_corrupt_pass_cannot_manufacture_personal_observation_on_export(self):
        checklist.save(self.root,SOURCE,'2'*64,{'observed':True,'entries':[self.entry()]})
        path=next((self.root/'Acceptance-Events').glob('*.json'));value=json.loads(path.read_text());value['personally_observed']=False;path.write_text(json.dumps(value))
        with self.assertRaises(ValueError):checklist.acceptance(self.root,SOURCE,'2'*64)

if __name__=='__main__':unittest.main()
