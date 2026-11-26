import importlib.util
import json
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch
import sys
from types import SimpleNamespace
ROOT=Path(__file__).resolve().parents[1]
sys.path.insert(0,str(ROOT/'scripts/build'))
import candidate_081 as candidate

class Candidate081Tests(unittest.TestCase):
    def test_namespace_stages_original_assets_without_mutating_or_admitting_old_channels(self):
        before=candidate.sha(ROOT/'msfs-toolbar/PackageSources/html_ui/InGamePanels/AeroScience/AeroScience.js')
        with tempfile.TemporaryDirectory() as d:
            stage=Path(d)/'stage';candidate.stage_toolbar(stage,'a'*40,'b'*32)
            panel=stage/'PackageSources/html_ui/InGamePanels/AeroScienceCandidateV081'
            script=(panel/'AeroScience.js').read_text()
            self.assertIn('AeroScience.Candidate.0_8_1.Command.v1',script)
            self.assertIn('validationSource:"'+'a'*40+'"',script);self.assertIn('validationInstance:"'+'b'*32+'"',script)
            self.assertNotIn('AeroScience.Validation.0_8_0.',script);self.assertNotIn('AeroScience.Command.v1',script)
            self.assertIn('PANEL_AEROSCIENCE_CANDIDATE_V081',(panel/'AeroScience.html').read_text())
            descriptor=candidate.read(panel/'toolbar-compatibility.json');self.assertEqual(descriptor['applicationVersion'],'0.8.1')
            self.assertTrue((stage/'PackageDefinitions/aeroscience-candidate-v0-8-1-toolbar.xml').exists())
            self.assertIn('Version="0.8.1"',(stage/'PackageDefinitions/aeroscience-candidate-v0-8-1-toolbar.xml').read_text())
            self.assertFalse((panel/'ProofOfConcept.js').exists())
            self.assertFalse(list(stage.rglob('*.spb')),'Source stage must never invent an SDK registration')
            with self.assertRaises(ValueError):candidate.stage_toolbar(stage,'a'*40,'b'*32)
        self.assertEqual(before,candidate.sha(ROOT/'msfs-toolbar/PackageSources/html_ui/InGamePanels/AeroScience/AeroScience.js'))
    def test_source_guard_rejects_wrong_branch_dirty_or_mismatched_source_and_allows_exact_detached(self):
        with tempfile.TemporaryDirectory() as d:
            repo=Path(d)
            def git(*a):return subprocess.check_output(['git','-C',d,*a],text=True,stderr=subprocess.DEVNULL).strip()
            git('init','-b','develop/v0.8.1');git('config','user.email','fixture@example.invalid');git('config','user.name','Disposable fixture')
            (repo/'one').write_text('original');git('add','one');git('commit','-m','fixture');source=git('rev-parse','HEAD')
            with patch.object(candidate,'BASELINE',source):
                candidate.source_check(source,repo)
                with self.assertRaises(ValueError):candidate.source_check('f'*40,repo)
                (repo/'one').write_text('dirty')
                with self.assertRaises(ValueError):candidate.source_check(source,repo)
                git('restore','one');git('switch','-c','main')
                with self.assertRaises(ValueError):candidate.source_check(source,repo)
                git('switch','--detach',source);candidate.source_check(source,repo)
    def test_fresh_output_and_protected_overlap_checks_do_not_create_rejected_destinations(self):
        with tempfile.TemporaryDirectory() as d,patch.object(candidate,'source_check'),patch.object(candidate.shutil,'disk_usage',return_value=SimpleNamespace(free=20*1024**3)):
            base=Path(d);out=base/'Candidate081-one'
            candidate.prepare(out,'a'*40,'b'*32)
            self.assertTrue((out/'BUILD-IN-PROGRESS.json').is_file())
            with self.assertRaises(ValueError):candidate.prepare(out,'a'*40,'b'*32)
            nested=out/'Candidate081-nested'
            with self.assertRaises(ValueError):candidate.prepare(nested,'a'*40,'b'*32)
            self.assertFalse(nested.exists())
            wrong=base/'Beta080-one'
            with self.assertRaises(ValueError):candidate.prepare(wrong,'a'*40,'b'*32)
            self.assertFalse(wrong.exists())
            with self.assertRaises(ValueError):candidate.prepare(base/'Candidate081-bad','a'*40,'0'*32)
    def test_insufficient_space_refuses_before_creating_an_attempt(self):
        with tempfile.TemporaryDirectory() as d,patch.object(candidate,'source_check'),patch.object(candidate.shutil,'disk_usage',return_value=SimpleNamespace(free=0)):
            output=Path(d)/'Candidate081-full'
            with self.assertRaisesRegex(ValueError,'12 GiB'):candidate.prepare(output,'a'*40,'b'*32)
            self.assertFalse(output.exists())
    def test_payload_links_and_case_aliases_cannot_hide_changes(self):
        with tempfile.TemporaryDirectory() as d:
            root=Path(d);(root/'application').mkdir();(root/'toolbar-source').mkdir();(root/'application/original').write_text('one')
            before=candidate.payload_inventory(root);(root/'application/original').write_text('changed');self.assertNotEqual(before,candidate.payload_inventory(root))
            if sys.platform!='win32':
                (root/'application/alias').symlink_to(root/'application/original')
                with self.assertRaises(ValueError):candidate.payload_inventory(root)
    def test_runtime_sdk_and_source_guards_are_not_replaced_by_structural_success(self):
        script=(ROOT/'scripts/build/Build-081Candidate.ps1').read_text()
        for required in ['--locked-mode','--profile-probe-081','--candidate-startup-081','Test-SimConnectRuntime.ps1','validate_runtime','RuntimeRedistributionConfirmed','Verify-And-Seal']:
            self.assertIn(required,script)
        self.assertNotIn('Build-BetaInstaller',script);self.assertNotIn('Start-Process',script)
        self.assertIn('validate_runtime',Path(candidate.__file__).read_text())
    def test_legacy_profile_graphs_keep_their_exact_versions_and_guards(self):
        props=(ROOT/'Directory.Build.props').read_text();self.assertIn('<Version>0.7.0</Version>',props)
        beta=(ROOT/'build/Beta080.props').read_text();self.assertIn('<Version>0.8.0-beta.1</Version>',beta)
        self.assertEqual(candidate.existing.BASELINE,'d50b3ea9c50e5e6e561aa77b2daaac4ad0864e5f')
        self.assertIn("branch in ('develop/v0.8.0', '')",(ROOT/'scripts/release/private_validation_080.py').read_text())
if __name__=='__main__':unittest.main()
