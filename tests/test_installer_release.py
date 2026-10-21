"""Installer integrity gates; these tests do not execute Windows setup."""
import shutil
import unittest
from unittest.mock import patch
import installer_fixtures as fixture
import test_release_flow as flow

i = fixture.installer


class InstallerReleaseTests(unittest.TestCase):
    def setUp(self):
        self.flow = flow.ReleaseFlowTests(); self.flow.setUp(); self.addCleanup(self.flow.tearDown)

    def test_finalization_preserves_exact_tested_exe_and_primary_download(self):
        folder = fixture.candidate(self.flow); meta = i.p.identity()
        before = (folder/meta['setup_name']).read_bytes()
        info = i.finalize(folder, self.flow.acceptance, self.flow.root/'final')
        final = self.flow.root/'final'
        self.assertEqual(before, (final/meta['setup_name']).read_bytes())
        self.assertEqual(info['asset_name'], i.p.identity()['setup_name'])
        self.assertIn('Run the setup EXE', (final/'RELEASE-NOTES.md').read_text(encoding='utf-8'))
        self.assertTrue((final/meta['setup_record_name']).is_file())
        with self.assertRaisesRegex(ValueError, 'already exist'): i.finalize(folder, self.flow.acceptance, final)

    def test_install_upgrade_and_uninstall_are_mandatory_even_for_alpha(self):
        folder = fixture.candidate(self.flow)
        original = i.p.read_json(self.flow.acceptance)
        for name in ('installer_install', 'setup_wizard', 'upgrade_preservation', 'uninstall_preservation'):
            data = i.p.read_json(self.flow.acceptance); data['checks'][name]['result'] = 'pending'; i.write_json(self.flow.acceptance, data)
            with self.assertRaisesRegex(ValueError, name): i.validate_candidate(folder, self.flow.acceptance)
            i.write_json(self.flow.acceptance, original)

    def test_altered_installer_cannot_reuse_old_acceptance_even_with_new_receipt(self):
        folder = fixture.candidate(self.flow); setup = folder/i.p.identity()['setup_name']
        with setup.open('ab') as output: output.write(b'changed after testing')
        receipt = i.p.read_json(folder/'build-receipt.json'); receipt['setup_sha256'] = i.sha(setup)
        i.write_json(folder/'build-receipt.json', receipt); i.p.write_checksum(setup)
        with self.assertRaisesRegex(ValueError, 'exact installer'): i.validate_candidate(folder, self.flow.acceptance)

    def test_changed_sdk_payload_and_wrong_prerequisite_signature_are_rejected(self):
        with self.assertRaisesRegex(ValueError, 'signed Microsoft'): fixture.candidate(self.flow, vc_signature='NotSigned')
        self.assertFalse((self.flow.root/'installer-candidate'/'build-receipt.json').exists())

    def test_installed_file_validation_detects_missing_and_changed_files(self):
        folder = fixture.candidate(self.flow); installed = self.flow.root/'installed'
        shutil.copytree(folder/'payload', installed)
        i.verify_installed(folder, installed)
        (installed/'AeroScience.Engine.dll').write_text('changed', encoding='utf-8')
        with self.assertRaisesRegex(ValueError, 'Installed file'): i.verify_installed(folder, installed)
        (folder/'payload'/'SimConnect.dll').write_text('changed', encoding='utf-8')
        with self.assertRaisesRegex(ValueError, 'loading/export|differ'): i.validate_candidate(folder)

    def test_same_recipe_in_a_crlf_checkout_keeps_tested_installer_valid(self):
        folder = fixture.candidate(self.flow)
        checkout = self.flow.root/'another checkout'; recipe = checkout/'scripts/installer/AeroScience.iss'
        recipe.parent.mkdir(parents=True)
        original = (i.p.REPO/'scripts/installer/AeroScience.iss').read_text(encoding='utf-8-sig')
        recipe.write_bytes(original.replace('\n', '\r\n').encode('utf-8'))
        assets = checkout/'src/AeroScience.Engine/wwwroot/assets-manifest.json'; assets.parent.mkdir(parents=True)
        assets.write_bytes((i.p.REPO/'src/AeroScience.Engine/wwwroot/assets-manifest.json').read_bytes())
        branding=assets.parent/'branding';branding.mkdir()
        shutil.copy2(i.p.REPO/'src/AeroScience.Engine/wwwroot/branding/manifest.json',branding/'manifest.json')
        approved=checkout/'assets/branding';approved.mkdir(parents=True)
        shutil.copy2(i.p.REPO/'assets/branding/orbital-arc-toolbar-white.svg',approved/'orbital-arc-toolbar-white.svg')
        with patch.object(i.p, 'REPO', checkout): i.validate_candidate(folder, self.flow.acceptance)


if __name__ == '__main__': unittest.main()
