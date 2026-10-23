"""Synthetic distribution, identity and filesystem tests; no native execution acceptance."""
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from test_reduced_native_imports import fixture

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('chart_runtime', ROOT/'scripts/build/chart_runtime.py')
runtime = importlib.util.module_from_spec(spec)
spec.loader.exec_module(runtime)


class ChartRuntimeTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.install = self.root/'installed'
        (self.install/'bin').mkdir(parents=True)
        for name in runtime.DLLS:
            (self.install/'bin'/name).write_bytes(fixture())
        (self.install/'share/proj').mkdir(parents=True)
        (self.install/'share/proj/proj.db').write_bytes(b'synthetic database identity, not PROJ acceptance')
        for component in runtime.COMPONENTS:
            p = self.install/'share'/component/'copyright'
            p.parent.mkdir(parents=True, exist_ok=True)
            p.write_text('Synthetic notice for '+component)
        (self.install/'share/gdal').mkdir(parents=True, exist_ok=True)
        (self.install/'share/gdal/gdalvrt.xsd').write_text('synthetic resource')
        self.recipe = self.root/'port.json'
        self.recipe.write_text(json.dumps({'vcpkgCommit': '1391150599c0b9dc6f2a3c79c50eff6c94b077d1',
                                         'gdalVersion': '3.12.4', 'jpegLibraryLinkage': 'static', 'turbojpegEnabled': False}))
        self.output = self.root/'bundle'

    def assemble(self):
        return runtime.assemble(self.install, self.output, self.recipe, 'a'*40)

    def test_complete_inventory_counts_data_notices_and_static_dependencies(self):
        result = self.assemble()
        self.assertGreater(result['totalBytes'], result['dllBytes'])
        self.assertIn('share/proj/proj.db', [r['path'] for r in result['files']])
        self.assertIn('licenses/libjpeg-turbo.txt', [r['path'] for r in result['files']])
        self.assertEqual(result, runtime.verify(self.output))
        self.assertFalse(result['publicDistributionApproved'])

    def test_changed_proj_database_or_missing_notice_fails_verification(self):
        self.assemble()
        p = self.output/'share/proj/proj.db'
        before = p.read_bytes()
        p.write_bytes(before+b'changed')
        with self.assertRaisesRegex(ValueError, 'content differs'):
            runtime.verify(self.output)
        p.write_bytes(before)
        (self.output/'licenses/libjpeg-turbo.txt').unlink()
        with self.assertRaises(FileNotFoundError):
            runtime.verify(self.output)

    def test_unlisted_file_and_manifest_traversal_are_rejected(self):
        self.assemble()
        rogue = self.output/'plugin.dll'
        rogue.write_bytes(fixture())
        with self.assertRaisesRegex(ValueError, 'Unlisted'):
            runtime.verify(self.output)
        rogue.unlink()
        p = self.output/'manifest.json'
        document = json.loads(p.read_text())
        document['files'][0]['path'] = '../outside.dll'
        p.write_text(json.dumps(document))
        with self.assertRaisesRegex(ValueError, 'Unsafe'):
            runtime.verify(self.output)

    def test_crt_failure_and_unknown_binary_cannot_be_packaged(self):
        (self.install/'bin/gdal.dll').write_bytes(fixture('vcruntime140.dll'))
        with self.assertRaisesRegex(ValueError, 'VC runtime'):
            self.assemble()
        self.assertFalse(self.output.exists())
        (self.install/'bin/gdal.dll').write_bytes(fixture())
        (self.install/'bin/plugin.dll').write_bytes(fixture())
        with self.assertRaisesRegex(ValueError, 'component graph'):
            self.assemble()

    def test_missing_static_notice_cannot_publish_partial_output(self):
        (self.install/'share/libjpeg-turbo/copyright').unlink()
        with self.assertRaises(FileNotFoundError):
            self.assemble()
        self.assertFalse(self.output.exists())

    def test_existing_bundle_is_preserved(self):
        self.assemble()
        before = (self.output/'manifest.json').read_bytes()
        with self.assertRaisesRegex(ValueError, 'Preserve'):
            self.assemble()
        self.assertEqual(before, (self.output/'manifest.json').read_bytes())

    def test_input_and_output_parent_links_are_rejected_before_writes(self):
        if not hasattr(__import__('os'), 'symlink'):
            self.skipTest('Platform has no symlink support')
        try:
            link = self.root/'link'
            link.symlink_to(self.install, target_is_directory=True)
        except OSError:
            self.skipTest('Platform requires unavailable symlink privilege')
        with self.assertRaisesRegex(ValueError, 'Linked/reparse'):
            runtime.assemble(link, self.output, self.recipe, 'a'*40)
        self.assertFalse(self.output.exists())
        with self.assertRaisesRegex(ValueError, 'Linked/reparse'):
            runtime.assemble(self.install, link/'escaped', self.recipe, 'a'*40)
        self.assertFalse((self.install/'escaped').exists())
        self.assemble()
        (self.output/'unlisted-directory').symlink_to(self.install, target_is_directory=True)
        with self.assertRaisesRegex(ValueError, 'Linked/reparse'):
            runtime.verify(self.output)

    def test_case_alias_cannot_duplicate_an_inventory_entry(self):
        self.assemble()
        p = self.output/'manifest.json'
        document = json.loads(p.read_text())
        document['files'].append(dict(document['files'][0], path=document['files'][0]['path'].upper()))
        p.write_text(json.dumps(document))
        with self.assertRaisesRegex(ValueError, 'duplicate'):
            runtime.verify(self.output)


if __name__ == '__main__':
    unittest.main()
