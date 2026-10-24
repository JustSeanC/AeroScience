"""Synthetic preparation/manifest controls. Actual pinned C# execution is a separate workflow."""
import importlib.util
import json
import subprocess
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('hdf_policy', ROOT/'tests/AeroScience.ManagedHdfProbe/prepare_hdf_read.py')
policy = importlib.util.module_from_spec(spec)
spec.loader.exec_module(policy)


class RestrictedHdfSourceTests(unittest.TestCase):
    def test_production_fork_verifies_all_pins_and_rejects_unreviewed_code(self):
        import shutil
        recipe = ROOT/'src/AeroScience.HdfReader/source-policy.json'
        original = ROOT/'src/AeroScience.HdfReader/Vendor/PureHDF'
        self.assertEqual(policy.verify(original, recipe)['verifiedFiles'], 275)
        config = json.loads(recipe.read_text())
        self.assertEqual(config['policyVersion'], 2)
        self.assertEqual(len(config['patchedSha256']), 6)
        with tempfile.TemporaryDirectory() as temporary:
            output = Path(temporary)/'fork'; shutil.copytree(original, output)
            (output/'Source/Filters/H5Filter.cs').write_bytes(b'UNREVIEWED')
            with self.assertRaises(ValueError):
                policy.verify(output, recipe)

    def test_synthetic_resource_and_numeric_originals_match_pinned_provenance(self):
        import hashlib
        for name in ['provenance.json', 'numeric-provenance.json']:
            info = json.loads((ROOT/'tests/AeroScience.HdfFixtures'/name).read_text())
            original = ROOT/'tests/AeroScience.HdfFixtures'/info['file']
            self.assertEqual(original.stat().st_size, info['bytes'])
            self.assertEqual(hashlib.sha256(original.read_bytes()).hexdigest(), info['sha256'])

    def test_exact_patch_rejects_missing_and_duplicate_sites(self):
        self.assertEqual(policy.replace_once('a POLICY b', 'POLICY', 'REJECT'), 'a REJECT b')
        for value in ['absent', 'POLICY POLICY']:
            with self.assertRaises(ValueError):
                policy.replace_once(value, 'POLICY', 'REJECT')

    def test_existing_output_is_never_replaced(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary); output = root/'preserved'; output.mkdir(); (output/'owner-data').write_bytes(b'preserve')
            with self.assertRaises(ValueError):
                policy.prepare(root, output)
            self.assertEqual((output/'owner-data').read_bytes(), b'preserve')

    def test_linked_parent_is_rejected_before_source_access(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary); (root/'original').mkdir()
            try:
                (root/'alias').symlink_to(root/'original', target_is_directory=True)
            except OSError:
                self.skipTest('Host does not permit developer-created directory links.')
            with self.assertRaises(ValueError):
                policy.ordinary(root/'alias'/'child')

    def fixture(self, root):
        output = root/'prepared'; (output/'Source').mkdir(parents=True)
        (output/'Source'/'Reader.cs').write_bytes(b'RESTRICTED')
        (output/'LICENSE.md').write_bytes(b'synthetic permissive notice')
        recipe = {'commit':'synthetic', 'files':{'Reader.cs':policy.digest(b'UPSTREAM')}, 'patchedSha256':{'Reader.cs':policy.digest(b'RESTRICTED')}, 'notices':{'LICENSE.md':policy.digest(b'synthetic permissive notice')}}
        file = root/'recipe.json'; file.write_text(json.dumps(recipe))
        (output/'policy-receipt.json').write_text(json.dumps({'upstreamCommit':'synthetic', 'productionApproved':False}))
        return output, file

    def test_compile_verification_checks_actual_source_again(self):
        with tempfile.TemporaryDirectory() as temporary:
            output, recipe = self.fixture(Path(temporary))
            with patch.object(policy, 'RECIPE', recipe):
                self.assertEqual(policy.verify(output)['verifiedFiles'], 1)
                (output/'Source'/'Reader.cs').write_bytes(b'UNRESTRICTED')
                with self.assertRaises(ValueError):
                    policy.verify(output)

    def test_unlisted_source_and_notice_tampering_fail_closed(self):
        for tamper in ['source', 'notice', 'approval']:
            with self.subTest(tamper=tamper), tempfile.TemporaryDirectory() as temporary:
                output, recipe = self.fixture(Path(temporary))
                if tamper == 'source':
                    (output/'Source'/'Extra.cs').write_bytes(b'unreviewed')
                elif tamper == 'notice':
                    (output/'LICENSE.md').write_bytes(b'truncated')
                else:
                    (output/'policy-receipt.json').write_text(json.dumps({'upstreamCommit':'synthetic', 'productionApproved':True}))
                with patch.object(policy, 'RECIPE', recipe), self.assertRaises(ValueError):
                    policy.verify(output)

    def test_actual_recipe_pins_all_reviewed_files_and_notices(self):
        recipe = json.loads(policy.RECIPE.read_text())
        self.assertEqual(len(recipe['files']), 275)
        self.assertEqual(len(recipe['patchedSha256']), 4)
        self.assertEqual(recipe['commit'], 'c2cea464ad2a6649258ab14c8d798cb6239b9e1e')
        for name, expected in recipe['notices'].items():
            self.assertEqual(policy.digest((ROOT/'tests/AeroScience.SafeHdfSource'/name).read_bytes()), expected)

    def test_windows_autocrlf_checkout_preserves_exact_reviewed_notice_bytes(self):
        recipe = json.loads(policy.RECIPE.read_text())
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary); repository = root/'source'; repository.mkdir()
            (repository/'.gitattributes').write_bytes((ROOT/'.gitattributes').read_bytes())
            for name in recipe['notices']:
                path = repository/'tests/AeroScience.SafeHdfSource'/name
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_bytes((ROOT/'tests/AeroScience.SafeHdfSource'/name).read_bytes())
            def git(*args):
                subprocess.run(['git', '-C', str(repository), '-c', 'core.autocrlf=true', *args], check=True, capture_output=True, text=True)
            git('init'); git('add', '.')
            output = root/'checkout'; output.mkdir()
            git('checkout-index', '--all', '--prefix='+output.as_posix()+'/')
            for name, expected in recipe['notices'].items():
                self.assertEqual(policy.digest((output/'tests/AeroScience.SafeHdfSource'/name).read_bytes()), expected)


if __name__ == '__main__':
    unittest.main()
