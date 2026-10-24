"""Synthetic preparation/manifest controls. Actual pinned C# execution is a separate workflow."""
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('hdf_policy', ROOT/'tests/AeroScience.ManagedHdfProbe/prepare_hdf_read.py')
policy = importlib.util.module_from_spec(spec)
spec.loader.exec_module(policy)


class RestrictedHdfSourceTests(unittest.TestCase):
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


if __name__ == '__main__':
    unittest.main()
