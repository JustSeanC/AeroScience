import hashlib
import importlib.util
import json
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest
ROOT=Path(__file__).resolve().parents[1]
WEB=ROOT/'src/AeroScience.Engine/wwwroot'
spec=importlib.util.spec_from_file_location('web_assets',ROOT/'scripts/build/verify_web_assets.py')
web_assets=importlib.util.module_from_spec(spec)
spec.loader.exec_module(web_assets)
class OfflineAssetsTests(unittest.TestCase):
    def test_committed_bundles_and_license_hashes_match(self):
        manifest=json.loads((WEB/'assets-manifest.json').read_text())
        for name,digest in manifest['files'].items():
            with self.subTest(name=name):self.assertEqual(hashlib.sha256((WEB/name).read_bytes()).hexdigest(),digest)
        for library in ('leaflet','leaflet-draw','chart.js','geographiclib'):
            self.assertGreater((WEB/'vendor'/library/'LICENSE.txt').stat().st_size,500)
    def test_real_geography_is_pinned_public_domain_and_contains_reference_airport(self):
        provenance=json.loads((WEB/'geography/provenance.json').read_text())
        self.assertEqual(provenance['license'],'Public domain')
        for data in provenance['datasets']:
            self.assertIn(provenance['upstream_commit'],data['upstream_url'])
            path=WEB/'geography'/data['file'];self.assertEqual(hashlib.sha256(path.read_bytes()).hexdigest(),data['bundled_sha256'])
            self.assertEqual(len(json.loads(path.read_text())['features']),data['features'])
        airports=json.loads((WEB/'geography/airports.geojson').read_text())['features']
        jfk=next(f for f in airports if f['properties']['gps_code']=='KJFK')
        self.assertAlmostEqual(jfk['geometry']['coordinates'][0],-73.78,delta=.1)
        self.assertAlmostEqual(jfk['geometry']['coordinates'][1],40.64,delta=.1)

    def test_modified_or_missing_assets_are_still_rejected(self):
        with tempfile.TemporaryDirectory(prefix='aeroscience-asset-integrity-') as temp:
            destination=Path(temp)/'wwwroot'
            shutil.copytree(WEB,destination)
            license_file=destination/'vendor/chart.js/LICENSE.txt'
            original=license_file.read_bytes()
            license_file.write_bytes(original.replace(b'\n',b'\r\n'))
            with self.assertRaisesRegex(ValueError,'vendor/chart.js/LICENSE.txt'):
                web_assets.verify(destination)
            license_file.write_bytes(original+b'changed')
            with self.assertRaisesRegex(ValueError,'vendor/chart.js/LICENSE.txt'):
                web_assets.verify(destination)
            license_file.unlink()
            with self.assertRaisesRegex(ValueError,'vendor/chart.js/LICENSE.txt'):
                web_assets.verify(destination)

    @unittest.skipUnless(shutil.which('git'),'Git is required for Windows checkout regression checks')
    def test_windows_git_checkout_and_existing_checkout_repair_preserve_all_pinned_bytes(self):
        self.verify_windows_checkout_and_repair(WEB)

    @unittest.skipUnless(shutil.which('git'),'Git is required for Windows checkout regression checks')
    def test_windows_checkout_repair_with_crlf_maintainer_manifest(self):
        # An older maintainer checkout can still have a CRLF manifest even
        # after pulling the LF attribute. Reproduce that input on every OS.
        with tempfile.TemporaryDirectory(prefix='aeroscience-crlf-source-') as temp:
            web=Path(temp)/'wwwroot'
            web.mkdir()
            for folder in ('vendor','geography'):
                shutil.copytree(WEB/folder,web/folder)
            manifest=(WEB/'assets-manifest.json').read_bytes().replace(b'\r\n',b'\n')
            (web/'assets-manifest.json').write_bytes(manifest.replace(b'\n',b'\r\n'))
            self.verify_windows_checkout_and_repair(web)

    def verify_windows_checkout_and_repair(self,source_web):
        # This source JSON is text eol=lf, unlike the byte-pinned upstream
        # assets. Seed canonical LF regardless of the maintainer's worktree;
        # committing raw CRLF with autocrlf=false would create a permanently
        # unnormalized index when the fixture later introduces the LF rule.
        manifest_bytes=(source_web/'assets-manifest.json').read_text(encoding='utf-8').encode('utf-8')
        with tempfile.TemporaryDirectory(prefix='aeroscience-windows-checkout-') as temp:
            temp=Path(temp)
            seed=temp/'seed'
            relative=Path('src/AeroScience.Engine/wwwroot')
            seed_web=seed/relative
            seed_web.mkdir(parents=True)
            empty_hooks=temp/'no-hooks'
            empty_hooks.mkdir()
            def git(*arguments,cwd=seed):
                return subprocess.run(['git','-c','commit.gpgSign=false',
                    '-c','core.hooksPath='+str(empty_hooks),*arguments],cwd=cwd,
                    check=True,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True)
            for folder in ('vendor','geography'):
                shutil.copytree(source_web/folder,seed_web/folder)
            (seed_web/'assets-manifest.json').write_bytes(manifest_bytes)
            git('-c','init.defaultBranch=main','init','-q')
            git('config','core.autocrlf','false')
            git('config','user.name','AeroScience checkout fixture')
            git('config','user.email','fixture@example.invalid')
            git('add','.')
            git('commit','-qm','Pinned asset fixture without checkout attributes')
            legacy=temp/'existing-windows-checkout'
            git('-c','core.autocrlf=true','clone','-q','--no-local',str(seed),str(legacy),cwd=temp)
            git('config','core.autocrlf','true',cwd=legacy)
            # The old checkout converts the source manifest too. Unlike the
            # pinned upstream files, its new rule requires LF, not -text.
            self.assertIn(b'\r\n',(legacy/relative/'assets-manifest.json').read_bytes())
            with self.assertRaisesRegex(ValueError,'vendor/chart.js/LICENSE.txt'):
                web_assets.verify(legacy/relative)

            # Pulling new attributes alone does not rewrite already converted files.
            shutil.copy2(ROOT/'.gitattributes',seed/'.gitattributes')
            git('add','.gitattributes')
            git('commit','-qm','Preserve exact pinned asset bytes')
            git('pull','--ff-only',cwd=legacy)
            # Reproduce the maintainer's staged CRLF changes: adding converted
            # files under the new byte-preserving attributes stages their CRLF bytes.
            git('add','--renormalize','--',(relative/'vendor').as_posix(),(relative/'geography').as_posix(),cwd=legacy)
            self.assertNotEqual(git('diff','--cached','--stat',cwd=legacy).stdout,'')
            # Restore the index AND worktree, explicitly disabling checkout EOL
            # conversion for this command only. No global Git setting is changed.
            # Git can consider a CRLF manifest clean after normalization and
            # skip rewriting it. Remove this tracked fixture file first so the
            # restore must recreate it using the new LF rule.
            (legacy/relative/'assets-manifest.json').unlink()
            git('-c','core.autocrlf=false','restore','--source=HEAD','--staged','--worktree','--',
                (relative/'vendor').as_posix(),(relative/'geography').as_posix(),
                (relative/'assets-manifest.json').as_posix(),cwd=legacy)
            self.assertEqual(web_assets.verify(legacy/relative),len(json.loads(manifest_bytes)['files']))
            self.assertEqual((legacy/relative/'assets-manifest.json').read_bytes(),
                manifest_bytes)
            self.assertEqual(git('status','--porcelain',cwd=legacy).stdout,'')
            self.assertEqual(git('config','core.autocrlf',cwd=legacy).stdout.strip(),'true')

            fresh=temp/'fresh-windows-checkout'
            git('-c','core.autocrlf=true','clone','-q','--no-local',str(seed),str(fresh),cwd=temp)
            git('config','core.autocrlf','true',cwd=fresh)
            self.assertEqual(web_assets.verify(fresh/relative),web_assets.verify(source_web))
            self.assertEqual((fresh/relative/'assets-manifest.json').read_bytes(),
                manifest_bytes)
            self.assertEqual(git('status','--porcelain',cwd=fresh).stdout,'')
            for name in json.loads(manifest_bytes)['files']:
                with self.subTest(asset=name):
                    self.assertEqual((fresh/relative/name).read_bytes(),(source_web/name).read_bytes())
