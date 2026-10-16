import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

spec = importlib.util.spec_from_file_location('packaging', Path(__file__).resolve().parents[1]/'scripts/release/package_release.py')
packaging = importlib.util.module_from_spec(spec); spec.loader.exec_module(packaging)


class PackagingTests(unittest.TestCase):
    def test_source_only_directory_is_rejected(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp); (root/'source.cs').write_text('source')
            with self.assertRaisesRegex(ValueError, 'manifest'): packaging.validate_community(root)
            with self.assertRaisesRegex(ValueError, 'self-contained'): packaging.validate_companion(root)

    def test_traversal_and_incorrect_file_sizes_are_rejected(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            (root/'manifest.json').write_text(json.dumps({'package_version': '0.1.0', 'content_type': 'MISC', 'title': 'AeroScience', 'minimum_game_version': '1.0.0.0', 'dependencies': []}))
            (root/'layout.json').write_text(json.dumps({'content': [{'path': '../escape', 'size': 0, 'date': 1}]}))
            with self.assertRaisesRegex(ValueError, 'Unsafe'): packaging.validate_community(root)
            (root/'asset').write_bytes(b'ab')
            (root/'layout.json').write_text(json.dumps({'content': [{'path': 'asset', 'size': 4, 'date': 1}]}))
            with self.assertRaisesRegex(ValueError, 'size'): packaging.validate_community(root)

    def test_proof_of_concept_receipt_and_pending_acceptance_cannot_be_released(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp); receipt = root/'receipt.json'; receipt.write_text('{"variant":"toolbar-poc"}')
            with self.assertRaisesRegex(ValueError, 'full-toolbar'): packaging.validate_receipt(receipt, {})
            acceptance = root/'acceptance.json'; acceptance.write_text(json.dumps({'version': '0.1.0', 'tester': 'test fixture', 'tested_utc': '2026-10-16', 'checks': {}}))
            with self.assertRaisesRegex(ValueError, 'pending'): packaging.validate_acceptance(acceptance)

    def test_archive_has_one_root_and_refuses_overwrite(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)/'AeroScience-fixture'; root.mkdir(); (root/'a.txt').write_text('fixture')
            output = Path(temp)/'fixture.zip'; packaging.zip_tree(root, output)
            with packaging.zipfile.ZipFile(output) as archive: self.assertEqual(archive.namelist(), ['AeroScience-fixture/a.txt'])
            with self.assertRaisesRegex(ValueError, 'overwrite'): packaging.zip_tree(root, output)


if __name__ == '__main__': unittest.main()
