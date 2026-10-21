"""Isolation/packaging fixtures only; no SDK or installed Windows acceptance."""
import importlib.util
import json
import os
from pathlib import Path
import shutil
import sys
import tempfile
import unittest
import subprocess
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
        self.assertIn('Publish-ValidationCompanion.ps1',script)
        self.assertIn("-ArtifactsPath (Join-Path $output 'intermediate') -OutputDirectory $app",script)
        self.assertIn('validate_runtime',script);self.assertIn('-ValidationProfile',script)
        self.assertNotIn('New-Release.ps1',script);self.assertNotIn('Complete-ReleaseAcceptance',script)
        self.assertNotIn('Set-ItemProperty',script);self.assertNotIn('AeroScience.iss',script)
        production=(ROOT/'scripts/installer/AeroScience.iss').read_text()
        self.assertIn('AppId={{BE9647D4-6501-48F2-BB5E-AEFAEC0F4542}',production)
        self.assertIn(r'DefaultDirName={localappdata}\Programs\AeroScience',production)
        self.assertNotIn('Validation',production)

    @unittest.skipUnless(shutil.which('pwsh'), 'PowerShell is required to exercise the publish command sequence')
    def test_validation_publish_restores_locked_graph_without_global_rid_then_never_restores_again(self):
        result, commands = self.publish_commands()
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertEqual(['restore', 'run', 'publish'], [c['argv'][0] for c in commands[:3]])
        restore, probe, publish = [c['argv'] for c in commands[:3]]
        self.assertEqual('AeroScience.sln', restore[1])
        self.assertIn('--locked-mode', restore)
        self.assertNotIn('-r', restore)
        self.assertFalse(any('RuntimeIdentifier' in a for a in restore))
        for args in (restore, probe, publish):
            self.assertIn('-p:AeroScienceValidation=true', args)
            self.assertEqual('isolated artifacts', args[args.index('--artifacts-path') + 1])
            self.assertFalse(any('force-evaluate' in a or 'LockedMode=false' in a for a in args))
        for args in (probe, publish):
            self.assertIn('--no-restore', args)
        self.assertEqual('win-x64', publish[publish.index('-r') + 1])
        self.assertEqual('true', publish[publish.index('--self-contained') + 1])
        self.assertIn('--profile-probe', probe)
        self.assertEqual(['scripts/build/verify_desktop_branding.py', '--folder', 'validation output'], commands[3]['argv'])

    @unittest.skipUnless(shutil.which('pwsh'), 'PowerShell is required to exercise publish failure propagation')
    def test_failed_restore_probe_or_publish_stops_before_following_commands(self):
        for failure, count in (('restore', 1), ('run', 2), ('publish', 3)):
            with self.subTest(failure=failure):
                result, commands = self.publish_commands(failure)
                self.assertNotEqual(0, result.returncode)
                self.assertEqual(count, len(commands))

    def publish_commands(self, failure=''):
        # Execute the production PowerShell script with recording native-command
        # substitutes. This checks argument boundaries/ordering/error handling;
        # hosted Windows separately executes real restore, probe and PE publishing.
        with tempfile.TemporaryDirectory() as tmp:
            log = Path(tmp)/'commands.json'
            environment = dict(os.environ, VALIDATION_TEST_ROOT=str(ROOT), VALIDATION_TEST_LOG=str(log), VALIDATION_FAIL_COMMAND=failure)
            driver = r'''
$global:commands = [Collections.Generic.List[object]]::new()
function global:dotnet {
    $global:commands.Add(@{tool='dotnet'; argv=@($args)})
    $global:LASTEXITCODE=0
    if ($args[0] -eq $env:VALIDATION_FAIL_COMMAND) { $global:LASTEXITCODE=23 }
}
function global:python {
    $global:commands.Add(@{tool='python'; argv=@($args)})
    $global:LASTEXITCODE=0
}
try {
    & (Join-Path $env:VALIDATION_TEST_ROOT 'scripts/build/Publish-ValidationCompanion.ps1') -ArtifactsPath 'isolated artifacts' -OutputDirectory 'validation output'
    $code=0
} catch { $code=1 }
ConvertTo-Json -InputObject @($global:commands) -Depth 8 | Set-Content -LiteralPath $env:VALIDATION_TEST_LOG -Encoding utf8
exit $code
'''
            result = subprocess.run(['pwsh', '-NoProfile', '-Command', driver], cwd=ROOT, env=environment, capture_output=True, text=True, timeout=30)
            return result, json.loads(log.read_text(encoding='utf-8-sig'))
