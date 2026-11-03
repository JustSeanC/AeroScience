"""Private beta control-flow fixtures. Not genuine SDK, Windows or installer acceptance."""
import json
from pathlib import Path
import shutil
import subprocess
import sys
import unittest
import installer_fixtures
import test_release_flow as flow

sys.path.insert(0, str(Path(__file__).resolve().parents[1]/'scripts/release'))
import beta_installer as beta


class BetaInstallerTests(unittest.TestCase):
    def setUp(self):
        self.flow = flow.ReleaseFlowTests()
        self.flow.setUp()
        self.addCleanup(self.flow.tearDown)
        self.app = self.flow.published
        toolbar = self.app/'Toolbar/aeroscience-validation-v0-7-0-toolbar'
        shutil.copytree(self.flow.sdk, toolbar)
        # Explicit structural fixture transformation, never a deliverable SDK output.
        for file in list(toolbar.rglob('*')):
            if not file.is_file():
                continue
            relative = file.relative_to(toolbar).as_posix()
            new = relative.replace('aeroscience_panel', 'aeroscience_validation_v070_panel').replace('InGamePanels/AeroScience/', 'InGamePanels/AeroScienceValidationV070/').replace('ICON_AEROSCIENCE', 'ICON_AEROSCIENCE_VALIDATION_V070')
            data = file.read_bytes()
            if file.suffix == '.html':
                data = data.replace(b'PANEL_AEROSCIENCE', b'PANEL_AEROSCIENCE_VALIDATION_V070')
            if file.name == 'AeroScience.js':
                data = b'// SYNTHETIC ONLY AeroScience.Validation.0_7_0.Command.v1 AeroScience.Validation.0_7_0.Reply.v1'
            target = toolbar/new
            target.parent.mkdir(parents=True, exist_ok=True)
            if target != file:
                file.unlink()
            target.write_bytes(data)
        manifest = beta.p.read_json(toolbar/'manifest.json')
        manifest['title'] = 'AeroScience 0.7.0 Validation'
        beta.write(toolbar/'manifest.json', manifest)
        beta.write(toolbar/'layout.json', dict(content=[dict(path=f.relative_to(toolbar).as_posix(), size=f.stat().st_size, date=1) for f in toolbar.rglob('*') if f.is_file() and f.name not in {'layout.json', 'manifest.json'}]))
        sdk = beta.p.read_json(self.flow.receipt)
        sdk.update(variant='toolbar-validation', files=beta.private.hashes(toolbar))
        sdk['native_javascript'] = dict(target='ES2017', compiler='TypeScript 5.9.3', parser='Acorn 8.15.0', files=[dict(path='PackageSources/'+name, sourceSha256='a'*64, compiledSha256=digest) for name, digest in sdk['files'].items() if name.endswith('.js')])
        beta.write(self.app/'Toolbar/sdk-build.json', sdk)
        info = beta.p.read_json(self.app/'build-info.json')
        info.update(profile='validation', public_release_approved=False)
        beta.write(self.app/'build-info.json', info)
        beta.write(self.app/'RUNTIME-AUTHORIZATION.json', beta.p.public_runtime_approval(beta.p.read_json(self.flow.approval)))
        for name in ('LICENSE', 'THIRD-PARTY-NOTICES.md'):
            shutil.copyfile(beta.p.REPO/name, self.app/name)
        beta.private.record(self.app, flow.COMMIT)
        self.output = self.flow.root/'beta'

    def stage(self):
        return beta.stage(self.app, self.output/'payload', flow.COMMIT, self.flow.report)

    def refresh_receipt(self):
        receipt = beta.p.read_json(self.app/'VALIDATION-RECEIPT.json')
        found = beta.private.hashes(self.app)
        found.pop('VALIDATION-RECEIPT.json')
        receipt['files'] = found
        beta.write(self.app/'VALIDATION-RECEIPT.json', receipt)

    def test_staging_keeps_exact_validator_and_sanitizes_runtime_paths_without_public_approval(self):
        before = beta.private.hashes(self.app)
        self.stage()
        self.assertEqual(before, beta.private.hashes(self.app))
        self.assertEqual(before, beta.private.hashes(self.output/'payload/application'))
        beta.payload(self.output/'payload', flow.COMMIT)
        report = (self.output/'payload/RUNTIME-CHECK.json').read_text()
        self.assertNotIn('runtime_file', report)
        self.assertNotIn('private', report)
        self.assertFalse(beta.p.read_json(self.output/'payload/BETA-BUILD.json')['public_release_approved'])
        with self.assertRaisesRegex(ValueError, 'cannot become public'):
            beta.p.validate_companion(self.output/'payload/application')
        with self.assertRaisesRegex(ValueError, 'already exists'):
            self.stage()

    def test_wrong_source_and_changed_originals_are_refused_without_new_payload(self):
        with self.assertRaisesRegex(ValueError, 'exact-source'):
            beta.stage(self.app, self.output/'payload', 'b'*40, self.flow.report)
        self.assertFalse(self.output.exists())
        (self.app/'AeroScience.Engine.dll').write_bytes(b'changed fixture')
        with self.assertRaisesRegex(ValueError, 'differ'):
            self.stage()

    def test_changed_sdk_receipt_and_personal_data_remain_refused_after_rehashing_outer_receipt(self):
        sdk_file = self.app/'Toolbar/sdk-build.json'
        original = sdk_file.read_bytes()
        sdk = beta.p.read_json(sdk_file)
        sdk['source_commit'] = 'b'*40
        beta.write(sdk_file, sdk)
        self.refresh_receipt()
        with self.assertRaisesRegex(ValueError, 'SDK build evidence'):
            self.stage()
        sdk_file.write_bytes(original)
        (self.app/'settings.json').write_text('{"personal":"must not ship"}')
        self.refresh_receipt()
        with self.assertRaisesRegex(ValueError, 'personal mission data'):
            self.stage()

    def test_metadata_only_or_wrong_runtime_cannot_be_distributed(self):
        report = beta.p.read_json(self.flow.report)
        report['export_check'] = 'not run'
        beta.write(self.flow.report, report)
        with self.assertRaisesRegex(ValueError, 'loading/export'):
            self.stage()

    def test_installer_hash_prerequisite_and_receipt_bind_exact_private_beta(self):
        self.stage()
        setup = self.output/beta.SETUP
        setup.write_bytes(installer_fixtures.executable_fixture())
        compiler = self.flow.root/'ISCC.exe'
        compiler.write_text('SYNTHETIC COMPILER FIXTURE ONLY')
        vc = self.flow.root/'VC_redist.x64.exe'
        vc.write_bytes(installer_fixtures.executable_fixture())
        tools = self.flow.root/'tools.json'
        report = dict(compiler_sha256=beta.installer.sha(compiler), compiler_version='6.3.3', vc_sha256=beta.installer.sha(vc), vc_version='14.0 TEST FIXTURE', vc_signature='NotSigned', vc_publisher='Microsoft Corporation - SYNTHETIC FIXTURE ONLY', vc_architecture='x64')
        beta.write(tools, report)
        with self.assertRaisesRegex(ValueError, 'signed Microsoft'):
            beta.record(self.output, flow.COMMIT, compiler, vc, tools)
        report['vc_signature'] = 'Valid'  # Control-flow fixture only, never an Authenticode result.
        beta.write(tools, report)
        beta.record(self.output, flow.COMMIT, compiler, vc, tools)
        beta.verify(self.output, flow.COMMIT)
        receipt = beta.p.read_json(self.output/beta.RECEIPT)
        self.assertFalse(receipt['public_release_approved'])
        self.assertEqual('Not verified', receipt['installed_acceptance'])
        setup.write_bytes(setup.read_bytes()+b'changed')
        with self.assertRaisesRegex(ValueError, 'checksum'):
            beta.verify(self.output, flow.COMMIT)

    def test_setup_guards_silent_install_and_preserves_shared_data_and_toolbar(self):
        text = (beta.p.REPO/'scripts/installer/AeroScienceBeta.iss').read_text()
        self.assertIn("InstallationAllowed(ExpandConstant('{app}'))", text)
        self.assertIn('InstallationAllowed(WizardDirValue)', text)
        self.assertIn('AeroScience-Validation-v0.7.0', text)
        self.assertIn('AeroScience-Beta-v0.7.0-beta.1', text)
        self.assertNotIn('[UninstallDelete]', text)
        self.assertNotIn('--uninstall-toolbar', text)
        self.assertNotIn('AppId={{BE9647D4', text)

    @unittest.skipUnless(shutil.which('pwsh') and sys.platform != 'win32', 'Linux PowerShell platform preflight')
    def test_windows_requirement_stops_before_creating_any_output(self):
        target = self.flow.root/'must-not-exist'
        result = subprocess.run(['pwsh', '-NoProfile', '-File', str(beta.p.REPO/'scripts/release/Build-BetaInstaller.ps1'), '-OutputDirectory', str(target), '-ValidationFolder', str(self.app), '-RuntimeReport', str(self.flow.report)], capture_output=True, text=True)
        self.assertNotEqual(0, result.returncode)
        self.assertIn('Build Beta 1 on Windows x64', result.stdout+result.stderr)
        self.assertFalse(target.exists())


if __name__ == '__main__':
    unittest.main()
