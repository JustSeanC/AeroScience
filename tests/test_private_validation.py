"""Isolation/packaging fixtures only; no SDK or installed Windows acceptance."""
import importlib.util
import json
from pathlib import Path
import shutil
import sys
import tempfile
import unittest
import test_packaging as fixtures

ROOT=Path(__file__).resolve().parents[1]
sys.path.insert(0,str(ROOT/'scripts/release'))
import private_validation as private
p=fixtures.packaging

class PrivateValidationTests(unittest.TestCase):
    def test_staged_namespace_is_complete_and_production_sources_unchanged(self):
        original=private.hashes(ROOT/'msfs-toolbar')
        with tempfile.TemporaryDirectory() as tmp:
            stage=Path(tmp)/'stage';shutil.copytree(ROOT/'msfs-toolbar',stage)
            private.stage_toolbar(stage,"a"*40)
            panel=stage/'PackageSources/InGamePanels/aeroscience_validation_panel.xml'
            self.assertIn('PANEL_AEROSCIENCE_VALIDATION',panel.read_text())
            self.assertIn('AeroScienceValidation/AeroScience.html',panel.read_text())
            base=stage/'PackageSources/html_ui/InGamePanels/AeroScienceValidation'
            js=(base/'AeroScience.js').read_text();html=(base/'AeroScience.html').read_text();css=(base/'AeroScience.css').read_text()
            self.assertNotIn('"AeroScience.Command.v1"',js);self.assertNotIn('"AeroScience.Reply.v1"',js)
            self.assertIn('AeroScience.Validation.0_5_0.Command.v1',js)
            self.assertIn('validationSource:"'+'a'*40+'"',js)
            for value in (js,html,css):self.assertIn('ingamepanel-aeroscience-validation',value)
            self.assertEqual((ROOT/'assets/branding/orbital-arc-toolbar-white.svg').read_bytes(),(stage/'PackageSources/html_ui/icons/toolbar/ICON_AEROSCIENCE_VALIDATION.svg').read_bytes())
            self.assertTrue((stage/'PackageDefinitions/aeroscience-validation-toolbar.xml').is_file())
        self.assertEqual(original,private.hashes(ROOT/'msfs-toolbar'))

    def test_production_gate_rejects_private_identity_before_packaging(self):
        with tempfile.TemporaryDirectory() as tmp:
            root=Path(tmp);(root/'build-info.json').write_text(json.dumps({'profile':'validation'}))
            with self.assertRaisesRegex(ValueError,'not a production release'):p.validate_companion(root)
            (root/'VALIDATION-RECEIPT.json').write_text('{}')
            with self.assertRaisesRegex(ValueError,'cannot become public'):p.validate_companion(root)

    def test_validation_structural_checks_reject_production_channels_and_bad_layout(self):
        with tempfile.TemporaryDirectory() as tmp:
            root=Path(tmp);fixtures.PackagingTests().community_fixture(root,'1.9.18')
            # Transform a synthetic compiled fixture, never pretend this is genuine SDK output.
            for file in sorted(root.rglob('*'),reverse=True):
                if not file.is_file():continue
                relative=file.relative_to(root).as_posix();new=relative.replace('aeroscience_panel','aeroscience_validation_panel').replace('InGamePanels/AeroScience/','InGamePanels/AeroScienceValidation/').replace('ICON_AEROSCIENCE','ICON_AEROSCIENCE_VALIDATION')
                data=file.read_bytes()
                if file.suffix=='.html':data=data.replace(b'PANEL_AEROSCIENCE',b'PANEL_AEROSCIENCE_VALIDATION')
                if file.name=='AeroScience.js':data=b'// AeroScience.Validation.0_5_0.Command.v1 AeroScience.Validation.0_5_0.Reply.v1'
                target=root/new;target.parent.mkdir(parents=True,exist_ok=True)
                if target!=file:file.unlink()
                target.write_bytes(data)
            manifest=json.loads((root/'manifest.json').read_text());manifest['title']='AeroScience 0.5.0 Validation';(root/'manifest.json').write_text(json.dumps(manifest))
            def layout():
                (root/'layout.json').write_text(json.dumps({'content':[dict(path=f.relative_to(root).as_posix(),size=f.stat().st_size,date=1) for f in root.rglob('*') if f.is_file() and f.name not in {'layout.json','manifest.json'}]}))
            layout();p.validate_community(root,validation=True)
            with self.assertRaises(ValueError):p.validate_community(root)
            js=root/'html_ui/InGamePanels/AeroScienceValidation/AeroScience.js';js.write_text(js.read_text()+' AeroScience.Command.v1');layout()
            with self.assertRaisesRegex(ValueError,'Production CommBus'):p.validate_community(root,validation=True)
            js.write_text('changed')
            with self.assertRaisesRegex(ValueError,'layout size'):p.validate_community(root,validation=True)

    def test_build_does_not_install_publish_or_relax_public_finalization(self):
        script=(ROOT/'scripts/release/Build-PrivateValidation.ps1').read_text()
        self.assertIn('-p:AeroScienceValidation=true',script);self.assertIn('--artifacts-path',script)
        self.assertIn('validate_runtime',script);self.assertIn('-ValidationProfile',script)
        self.assertNotIn('New-Release.ps1',script);self.assertNotIn('Complete-ReleaseAcceptance',script)
        self.assertNotIn('Set-ItemProperty',script);self.assertNotIn('AeroScience.iss',script)
        production=(ROOT/'scripts/installer/AeroScience.iss').read_text()
        self.assertIn('AppId={{BE9647D4-6501-48F2-BB5E-AEFAEC0F4542}',production)
        self.assertIn(r'DefaultDirName={localappdata}\Programs\AeroScience',production)
        self.assertNotIn('Validation',production)
