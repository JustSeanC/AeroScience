import importlib.util
import json
from pathlib import Path
import tempfile
import struct
import subprocess
import sys
import unittest
import shutil

spec = importlib.util.spec_from_file_location('packaging', Path(__file__).resolve().parents[1]/'scripts/release/package_release.py')
packaging = importlib.util.module_from_spec(spec); spec.loader.exec_module(packaging)


class PackagingTests(unittest.TestCase):
    def companion_fixture(self, root):
        # Minimal structural PE fixture; never evidence of Windows execution.
        (root/'wwwroot').mkdir(parents=True)
        web=packaging.REPO/'src/AeroScience.Engine/wwwroot'
        for directory in ('vendor','geography'):
            shutil.copytree(web/directory,root/'wwwroot'/directory)
        shutil.copy2(web/'assets-manifest.json',root/'wwwroot/assets-manifest.json')
        for name in ('patterns.js','analysis.js','science-display.js'):
            shutil.copy2(web/name,root/'wwwroot'/name)
        image = bytearray(512); image[:2] = b'MZ'; struct.pack_into('<I', image, 60, 128)
        image[128:132] = b'PE\0\0'; struct.pack_into('<H', image, 132, 0x8664)
        struct.pack_into('<H', image, 152, 0x20b); struct.pack_into('<H', image, 220, 2)
        (root/'AeroScience.exe').write_bytes(image)
        for name in ('AeroScience.Engine.dll', 'AeroScience.dll', 'AeroScience.deps.json', 'AeroScience.runtimeconfig.json',
                     'coreclr.dll', 'hostpolicy.dll', 'System.Windows.Forms.dll', 'wwwroot/index.html', 'wwwroot/app.js', 'wwwroot/app.css', 'wwwroot/research.js', 'wwwroot/map.js'):
            (root/name).write_text('test fixture')

    def community_fixture(self, root, minimum_game_version, lowercase_assets=False):
        # Structural test data only; this is not an SDK output or simulator acceptance fixture.
        manifest = {'package_version': packaging.VERSION, 'content_type': 'MISC', 'title': 'AeroScience',
                    'minimum_game_version': minimum_game_version, 'dependencies': []}
        (root/'manifest.json').write_text(json.dumps(manifest))
        assets = {
            'html_ui/InGamePanels/AeroScience/toolbar-compatibility.json': json.dumps({'protocol':1,'applicationVersion':packaging.VERSION}).encode(),
            'InGamePanels/aeroscience_panel.spb': b'\x00SPB structural test fixture' * 3,
            'html_ui/InGamePanels/AeroScience/AeroScience.html':
                b'<ingame-ui panel-id="PANEL_AEROSCIENCE">AeroScience.js AeroScience.css</ingame-ui>',
            'html_ui/InGamePanels/AeroScience/AeroScience.js': b'// test fixture',
            'html_ui/InGamePanels/AeroScience/AeroScience.css': b'/* test fixture */',
            'html_ui/InGamePanels/AeroScience/ProofOfConcept.html': b'<p>Diagnostic fixture</p>',
            'html_ui/InGamePanels/AeroScience/ProofOfConcept.js': b'// diagnostic fixture',
            'html_ui/icons/toolbar/ICON_AEROSCIENCE.svg': b'<svg/>',
        }
        if lowercase_assets:
            assets = {name.lower(): content for name, content in assets.items()}
        for name, content in assets.items():
            path = root/name; path.parent.mkdir(parents=True, exist_ok=True); path.write_bytes(content)
        (root/'layout.json').write_text(json.dumps({'content': [
            {'path': name, 'size': len(content), 'date': 1} for name, content in assets.items()
        ]}))

    def test_lowercase_layout_resolves_mixed_case_files_without_changing_them(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp); self.community_fixture(root, '1.9.18')
            layout = json.loads((root/'layout.json').read_text())
            for entry in layout['content']:
                entry['path'] = entry['path'].lower().replace('/', '\\')
            (root/'layout.json').write_text(json.dumps(layout))
            before = {name: path.read_bytes() for name, path in packaging.files(root).items()}
            found = packaging.validate_community(root)
            self.assertIn('html_ui/icons/toolbar/ICON_AEROSCIENCE.svg', found)
            self.assertEqual({name: path.read_bytes() for name, path in found.items()}, before)

    def test_expected_assets_resolve_when_sdk_output_names_are_lowercase(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp); self.community_fixture(root, '1.9.18', lowercase_assets=True)
            found = packaging.validate_community(root)
            self.assertIn('html_ui/ingamepanels/aeroscience/aeroscience.html', found)

    def test_case_only_duplicate_layout_entries_are_rejected(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp); self.community_fixture(root, '1.9.18')
            layout = json.loads((root/'layout.json').read_text())
            alias = dict(layout['content'][0]); alias['path'] = alias['path'].lower()
            layout['content'].append(alias)
            (root/'layout.json').write_text(json.dumps(layout))
            with self.assertRaisesRegex(ValueError, 'Duplicate layout entry'):
                packaging.validate_community(root)

    def test_case_colliding_files_cannot_share_one_layout_entry(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp); self.community_fixture(root, '1.9.18')
            alias = root/'html_ui/icons/toolbar/icon_aeroscience.svg'
            if alias.exists(): self.skipTest('A case-sensitive filesystem is required to create both aliases.')
            alias.write_text('<svg/>')
            with self.assertRaisesRegex(ValueError, 'case-colliding'):
                packaging.validate_community(root)

    def test_case_insensitive_matching_still_rejects_missing_files_and_wrong_sizes(self):
        for missing in (False, True):
            with self.subTest(missing=missing), tempfile.TemporaryDirectory() as temp:
                root = Path(temp); self.community_fixture(root, '1.9.18')
                layout = json.loads((root/'layout.json').read_text())
                for entry in layout['content']: entry['path'] = entry['path'].lower()
                if missing:
                    (root/'html_ui/icons/toolbar/ICON_AEROSCIENCE.svg').unlink()
                else:
                    layout['content'][-1]['size'] += 1
                (root/'layout.json').write_text(json.dumps(layout))
                with self.assertRaisesRegex(ValueError, 'missing or invalid file' if missing else 'Incorrect layout size'):
                    packaging.validate_community(root)

    def test_layout_cannot_reference_itself_using_another_case(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp); self.community_fixture(root, '1.9.18')
            layout = json.loads((root/'layout.json').read_text())
            layout['content'].append({'path': 'LAYOUT.JSON', 'size': 1, 'date': 1})
            (root/'layout.json').write_text(json.dumps(layout))
            with self.assertRaisesRegex(ValueError, 'missing or invalid file'):
                packaging.validate_community(root)

    def test_three_and_four_component_sdk_game_versions_are_accepted(self):
        for version in ('1.9.18', '1.9.18.0'):
            with self.subTest(version=version), tempfile.TemporaryDirectory() as temp:
                root = Path(temp); self.community_fixture(root, version)
                self.assertIn('InGamePanels/aeroscience_panel.spb', packaging.validate_community(root))

    def test_missing_or_malformed_sdk_game_versions_are_rejected(self):
        for version in (None, '', 10918, [], {}, '1.9', '1.9.x', '1.9.18.0.1', 'v1.9.18', '1.9.18\n'):
            with self.subTest(version=version), tempfile.TemporaryDirectory() as temp:
                root = Path(temp); self.community_fixture(root, version)
                with self.assertRaisesRegex(ValueError, 'minimum_game_version'):
                    packaging.validate_community(root)
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp); self.community_fixture(root, '1.9.18')
            manifest = json.loads((root/'manifest.json').read_text()); manifest.pop('minimum_game_version')
            (root/'manifest.json').write_text(json.dumps(manifest))
            with self.assertRaisesRegex(ValueError, 'minimum_game_version'):
                packaging.validate_community(root)

    def test_companion_preview_refuses_a_proprietary_runtime_in_input(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp); companion = root/'published'; self.companion_fixture(companion)
            (companion/'SIMCONNECT.DLL').write_text('test fixture')
            result = subprocess.run([sys.executable, str(packaging.REPO/'scripts/release/package_release.py'), 'companion-preview',
                                     '--companion', str(companion), '--output', str(root/'Preview.zip')], capture_output=True, text=True)
            self.assertNotEqual(result.returncode, 0)
            self.assertIn('exclude the proprietary', result.stderr)
            self.assertFalse((root/'Preview.zip').exists())

    def test_preview_has_one_graphical_entry_point_and_checksum_without_terminal_helpers(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp); companion = root/'published'; self.companion_fixture(companion)
            output = root/'Companion-Preview.zip'
            subprocess.run([sys.executable, str(packaging.REPO/'scripts/release/package_release.py'), 'companion-preview',
                            '--companion', str(companion), '--output', str(output)], check=True, capture_output=True, text=True)
            with packaging.zipfile.ZipFile(output) as archive:
                names = archive.namelist()
                executable = f'AeroScience-Companion-Preview-v{packaging.RELEASE_VERSION}/AeroScience.exe'
                self.assertIn(executable, names)
                self.assertEqual([name for name in names if name.endswith('.exe')], [executable])
                self.assertFalse(any(name.endswith(('.cmd', '.ps1')) for name in names))
                self.assertTrue(any(name.endswith('/PREVIEW.txt') for name in names))
            self.assertEqual(output.with_suffix('.zip.sha256').read_text().split()[0], packaging.hashlib.sha256(output.read_bytes()).hexdigest())

    def test_companion_rejects_console_entry_test_assemblies_and_personal_data(self):
        for forbidden in ('nested/AEROSCIENCE.TESTHOST.DLL', 'Launch-Simulated.cmd', 'missions.db', 'settings.json', 'engine.log'):
            with self.subTest(forbidden=forbidden), tempfile.TemporaryDirectory() as temp:
                root = Path(temp); self.companion_fixture(root)
                extra = root/forbidden; extra.parent.mkdir(parents=True, exist_ok=True); extra.write_text('must not ship')
                with self.assertRaisesRegex(ValueError, 'developer/test|personal mission'):
                    packaging.validate_companion(root)
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp); self.companion_fixture(root)
            image = bytearray((root/'AeroScience.exe').read_bytes()); struct.pack_into('<H', image, 220, 3)
            (root/'AeroScience.exe').write_bytes(image)
            with self.assertRaisesRegex(ValueError, 'graphical Windows'):
                packaging.validate_companion(root)

    def test_full_toolbar_receipt_requires_installed_header_contract_and_matching_files(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp); self.community_fixture(root, '1.9.18'); found = packaging.validate_community(root)
            path = root/'receipt.json'
            receipt = {'variant': 'toolbar-full', 'built_utc': 'fixture date', 'builder_sha256': 'fixture hash',
                       'files': {name: packaging.hashlib.sha256(file.read_bytes()).hexdigest() for name, file in found.items()}}
            path.write_text(json.dumps(receipt))
            with self.assertRaisesRegex(ValueError, 'ABI contract'):
                packaging.validate_receipt(path, found)
            receipt.update(sdk_contract='passed', sdk_header_sha256='structural fixture hash')
            path.write_text(json.dumps(receipt)); packaging.validate_receipt(path, found)
            found['html_ui/InGamePanels/AeroScience/AeroScience.js'].write_text('altered after receipt')
            with self.assertRaisesRegex(ValueError, 'differ'):
                packaging.validate_receipt(path, found)

    def test_source_only_directory_is_rejected(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp); (root/'source.cs').write_text('source')
            with self.assertRaisesRegex(ValueError, 'manifest'): packaging.validate_community(root)
            with self.assertRaisesRegex(ValueError, 'self-contained'): packaging.validate_companion(root)

    def test_traversal_and_incorrect_file_sizes_are_rejected(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            (root/'manifest.json').write_text(json.dumps({'package_version': packaging.VERSION, 'content_type': 'MISC', 'title': 'AeroScience', 'minimum_game_version': '1.0.0.0', 'dependencies': []}))
            (root/'layout.json').write_text(json.dumps({'content': [{'path': '../escape', 'size': 0, 'date': 1}]}))
            with self.assertRaisesRegex(ValueError, 'Unsafe'): packaging.validate_community(root)
            (root/'asset').write_bytes(b'ab')
            (root/'layout.json').write_text(json.dumps({'content': [{'path': 'asset', 'size': 4, 'date': 1}]}))
            with self.assertRaisesRegex(ValueError, 'size'): packaging.validate_community(root)

    def test_proof_of_concept_receipt_and_pending_acceptance_cannot_be_released(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp); receipt = root/'receipt.json'; receipt.write_text('{"variant":"toolbar-poc"}')
            with self.assertRaisesRegex(ValueError, 'full-toolbar'): packaging.validate_receipt(receipt, {})
            acceptance = root/'acceptance.json'; acceptance.write_text(json.dumps({'version': packaging.RELEASE_VERSION, 'tester': 'test fixture', 'tested_utc': '2026-10-17T00:00:00Z', 'source_commit': 'a'*40, 'checks': {}}))
            with self.assertRaisesRegex(ValueError, 'pending'): packaging.validate_acceptance(acceptance)

    def test_archive_has_one_root_and_refuses_overwrite(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)/'AeroScience-fixture'; root.mkdir(); (root/'a.txt').write_text('fixture')
            output = Path(temp)/'fixture.zip'; packaging.zip_tree(root, output)
            with packaging.zipfile.ZipFile(output) as archive: self.assertEqual(archive.namelist(), ['AeroScience-fixture/a.txt'])
            with self.assertRaisesRegex(ValueError, 'overwrite'): packaging.zip_tree(root, output)

    def test_window_probe_receipt_cannot_be_released_as_full_toolbar(self):
        with tempfile.TemporaryDirectory() as temp:
            receipt = Path(temp)/'receipt.json'
            receipt.write_text('{"variant":"toolbar-window-probe"}')
            with self.assertRaisesRegex(ValueError, 'full-toolbar'):
                packaging.validate_receipt(receipt, {})

    def test_stable_acceptance_requires_vr_performance_evidence(self):
        with tempfile.TemporaryDirectory() as temp:
            acceptance = Path(temp)/'acceptance.json'
            data = {'version': '1.0.0', 'source_commit': 'a'*40, 'tester': 'test fixture', 'tested_utc': '2026-10-17T00:00:00Z',
                    'checks': {name: {'result': 'passed', 'evidence': 'test fixture'} for name in packaging.ACCEPTANCE}}
            data['checks'].pop('vr_performance')
            data['checks']['vr_quest3'] = {'result': 'passed', 'evidence': 'obsolete interaction record'}
            acceptance.write_text(json.dumps(data))
            with self.assertRaisesRegex(ValueError, 'vr_performance'):
                packaging.validate_acceptance(acceptance, version='1.0.0')
            data['checks']['vr_performance'] = {'result': 'passed', 'evidence': 'frame-time and resource comparison fixture'}
            acceptance.write_text(json.dumps(data))
            packaging.validate_acceptance(acceptance, version='1.0.0')


if __name__ == '__main__': unittest.main()
