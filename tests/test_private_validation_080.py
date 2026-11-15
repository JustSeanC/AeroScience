"""P08b safety/structural fixtures. Never native SDK/MSFS acceptance receipts."""
import copy
import json
import os
from pathlib import Path
import shutil
import struct
import subprocess
import sys
import tempfile
import unittest
from unittest import mock

ROOT=Path(__file__).resolve().parents[1]
sys.path.insert(0,str(ROOT/'scripts/release'))
import private_validation_080 as v
import package_release as public

COMMIT='a'*40
INSTANCE='1'*32

class Private080Tests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.temp=tempfile.TemporaryDirectory(prefix='p08b-structural-')
        cls.stage=Path(cls.temp.name)/'stage'
        cls.original=v.inventory(ROOT/'msfs-toolbar')
        v.stage_toolbar(cls.stage,COMMIT,INSTANCE)
        subprocess.run(['node',str(ROOT/'scripts/build/native-javascript.mjs'),str(cls.stage)],check=True,capture_output=True,text=True,timeout=60,cwd=ROOT)
    @classmethod
    def tearDownClass(cls):
        cls.temp.cleanup()

    def fixture(self,root):
        # Deliberately synthetic SPB bytes: structural validation is not SDK proof.
        package=root/'package';shutil.copytree(self.stage/'PackageSources',package)
        registration=package/'InGamePanels/aeroscience_validation_v080_panel.xml'
        registration.unlink();registration.with_suffix('.spb').write_bytes(b'SYNTHETIC-STRUCTURAL-ONLY\0'+bytes(64))
        (package/'manifest.json').write_text(json.dumps(dict(title=v.TITLE,package_version='0.8.0',minimum_game_version='1.9.21',content_type='MISC',dependencies=[])), encoding='utf-8')
        self.layout(package)
        return package
    def layout(self,package):
        rows=[dict(path=p.relative_to(package).as_posix(),size=p.stat().st_size,date=1) for p in package.rglob('*') if p.is_file() and p.name not in ['layout.json','manifest.json']]
        (package/'layout.json').write_text(json.dumps({'content':rows}), encoding='utf-8')

    def test_staging_changes_only_fresh_copy_and_preserves_bootstrap_protocol_identity(self):
        self.assertEqual(self.original,v.inventory(ROOT/'msfs-toolbar'))
        panel=self.stage/'PackageSources/html_ui/InGamePanels'/v.PANEL
        script=(panel/'AeroScience.js').read_text(encoding='utf-8')
        for token in ['AeroScience.Validation.0_8_0.Command.v1','AeroScience.Validation.0_8_0.Reply.v1',COMMIT,INSTANCE,'validationSource','validationInstance']:
            self.assertIn(token,script)
        self.assertNotIn('AeroScience.Command.v1',script)
        self.assertIn('TemplateElement',script)
        self.assertIn('IngamePanelAeroScienceValidationV080',script)
        self.assertTrue((panel/'AeroScience.LocaleData.js').is_file())
        descriptor=v.read(panel/'toolbar-compatibility.json');self.assertEqual(INSTANCE,descriptor['validationInstance']);self.assertEqual('0.8.0',descriptor['applicationVersion'])
        with self.assertRaisesRegex(ValueError,'earlier SDK stage'):v.stage_toolbar(self.stage,COMMIT,INSTANCE)

    def test_staging_preserves_utf8_geography_with_windows_cp1252_default(self):
        original_read=Path.read_text
        def windows_default(path, encoding=None, errors=None):
            return original_read(path,encoding='cp1252' if encoding is None else encoding,errors=errors)
        with tempfile.TemporaryDirectory() as t:
            stage=Path(t)/'stage'
            with mock.patch.object(Path,'read_text',new=windows_default):
                v.stage_toolbar(stage,COMMIT,INSTANCE)
            geography=ROOT/'src/AeroScience.Engine/wwwroot/geography'
            expected='\n'.join('window.AeroScience'+var+' = '+original_read(geography/(name+'.geojson'),encoding='utf-8')+';' for name,var in [('countries','Countries'),('places','Places'),('airports','Airports')])
            actual=stage/'PackageSources/html_ui/InGamePanels'/v.PANEL/'AeroScience.Geography.js'
            self.assertTrue(any(ord(character)>127 for character in expected),'Fixture must exercise genuine non-ASCII geography labels.')
            self.assertEqual(expected.encode('utf-8'),actual.read_bytes())

    def test_structural_validator_rejects_foreign_channels_attempt_and_placeholder_or_missing_registration(self):
        with tempfile.TemporaryDirectory() as t:
            package=self.fixture(Path(t));v.validate_community(package,COMMIT,INSTANCE)
            with self.assertRaisesRegex(ValueError,'source/attempt'):v.validate_community(package,COMMIT,'2'*32)
            js=package/'html_ui/InGamePanels'/v.PANEL/'AeroScience.js';original=js.read_text(encoding='utf-8');js.write_text(original+'\n// AeroScience.Command.v1', encoding='utf-8');self.layout(package)
            with self.assertRaisesRegex(ValueError,'CommBus'):v.validate_community(package,COMMIT,INSTANCE)
            js.write_text(original, encoding='utf-8')
            spb=package/'InGamePanels/aeroscience_validation_v080_panel.spb';spb.write_text('<xml>'+('x'*64), encoding='utf-8');self.layout(package)
            with self.assertRaisesRegex(ValueError,'placeholder'):v.validate_community(package,COMMIT,INSTANCE)
            spb.unlink();self.layout(package)
            with self.assertRaisesRegex(ValueError,'registration'):v.validate_community(package,COMMIT,INSTANCE)

    def test_layout_traversal_size_missing_assets_and_stale_js_proof_refuse(self):
        with tempfile.TemporaryDirectory() as t:
            package=self.fixture(Path(t));layout=v.read(package/'layout.json');layout['content'][0]['path']='../escape'
            (package/'layout.json').write_text(json.dumps(layout), encoding='utf-8')
            with self.assertRaisesRegex(ValueError,'Unsafe'):v.validate_community(package,COMMIT,INSTANCE)
            self.layout(package);layout=v.read(package/'layout.json');layout['content'][0]['size']+=1;(package/'layout.json').write_text(json.dumps(layout), encoding='utf-8')
            with self.assertRaisesRegex(ValueError,'size/date'):v.validate_community(package,COMMIT,INSTANCE)
            self.layout(package)
            from private_validation import validate_native_javascript
            proof=v.read(self.stage/'native-javascript.json');assets=v.inventory(package)
            validate_native_javascript(proof,assets)
            assets['html_ui/InGamePanels/'+v.PANEL+'/AeroScience.js']='f'*64
            with self.assertRaisesRegex(ValueError,'bytes differ'):validate_native_javascript(proof,assets)

    def test_locale_catalogs_bind_native_loose_and_actual_embedded_bytes(self):
        with tempfile.TemporaryDirectory() as t:
            root=Path(t);package=self.fixture(root)
            target=root/'App/wwwroot/i18n';shutil.copytree(ROOT/'src/AeroScience.Engine/wwwroot/i18n',target)
            embedded={name:v.sha(target/relative) for name,relative in [('manifest','locale-manifest.json'),('schema','message-schema.json')]+[(x,'locales/'+x+'.json') for x in v.LOCALES]}
            v.catalogs_check(root/'App',package,embedded)
            bad=copy.deepcopy(embedded);bad['de-DE']='f'*64
            with self.assertRaisesRegex(ValueError,'embedded'):v.catalogs_check(root/'App',package,bad)
            manifest=v.read(target/'locale-manifest.json');manifest['catalogVersion']-=1;(target/'locale-manifest.json').write_text(json.dumps(manifest), encoding='utf-8')
            with self.assertRaisesRegex(ValueError,'Stale'):v.catalogs_check(root/'App',package,embedded)

    def test_output_blocks_existing_attempts_data_sdk_checkout_community_relative_and_links(self):
        with tempfile.TemporaryDirectory() as t:
            root=Path(t);repo=root/'repo';sdk=root/'sdk';data=root/'data'
            for p in [repo,sdk,data]:p.mkdir()
            okay=root/'Validation080-new';self.assertEqual(okay,v.safe_output(okay,repo,sdk,[data]))
            for parent in [repo,sdk,data,root/'Community']:
                parent.mkdir(exist_ok=True)
                with self.assertRaises(ValueError):v.safe_output(parent/'Validation080-new',repo,sdk,[data])
            okay.mkdir()
            with self.assertRaisesRegex(ValueError,'already exists'):v.safe_output(okay,repo,sdk,[data])
            with self.assertRaisesRegex(ValueError,'absolute'):v.safe_output('Validation080-relative',repo,sdk,[data])
            target=root/'linked';target.mkdir();link=root/'link'
            if os.name=='nt':subprocess.run(['cmd.exe','/d','/c','mklink','/J',str(link),str(target)],check=True,capture_output=True)
            else:link.symlink_to(target,target_is_directory=True)
            with self.assertRaisesRegex(ValueError,'Links/junctions'):v.safe_output(link/'Validation080-new',repo,sdk,[])
            self.assertTrue(v.overlap(r'c:\USER\Science',r'C:\user\Science\Records'))
            self.assertFalse(v.overlap(r'C:\data\Science',r'C:\data\Science-old'))

    def test_existing_tester_attempts_and_incomplete_prerequisites_are_not_reused(self):
        with tempfile.TemporaryDirectory() as t:
            root=Path(t);repo=root/'repo';sdk=root/'sdk';repo.mkdir();sdk.mkdir()
            existing=root/'AeroScience-Builds'/'Beta2'/'installed';existing.mkdir(parents=True)
            with self.assertRaisesRegex(ValueError,'tester/validator'):v.safe_output(existing/'Validation080-bad',repo,sdk,[])
            other=root/'Tester';other.mkdir();(other/'AeroScience.exe').write_bytes(b'SYNTHETIC original sentinel')
            with self.assertRaisesRegex(ValueError,'tester installation'):v.safe_output(other/'Validation080-bad',repo,sdk,[])
            self.assertEqual(b'SYNTHETIC original sentinel',(other/'AeroScience.exe').read_bytes())
            with mock.patch.object(v,'source_check',return_value=COMMIT),mock.patch.object(v,'protected_roots',return_value=[]):
                proof=v.preflight(repo,COMMIT,sdk,root/'missing-approval.json',root/'Validation080-not-built',False)
                self.assertFalse(proof['ready']);self.assertEqual('Not Tested',proof['installed_acceptance'])
                by_name={c['name']:c for c in proof['checks']}
                self.assertEqual('Blocked',by_name['genuine-SDK-inputs']['outcome']);self.assertEqual('Blocked',by_name['native-runtime-loading-exports-authorization']['outcome'])
                with self.assertRaisesRegex(ValueError,'successful matching'):v.begin(repo,COMMIT,sdk,root/'Validation080-not-built',INSTANCE,proof)
                self.assertFalse((root/'Validation080-not-built').exists())

    def test_public_runtime_authorization_must_match_actual_bytes_and_export_report(self):
        with tempfile.TemporaryDirectory() as t:
            root=Path(t);(root/'SimConnect.dll').write_bytes(b'SYNTHETIC AUTHORIZATION-REFUSAL FIXTURE, NOT A DLL')
            report=root/'report.json';approval=root/'approval.json';report.write_text(json.dumps({'architecture':'Windows x64','export_check':'not run'}), encoding='utf-8');approval.write_text('{}', encoding='utf-8')
            with self.assertRaisesRegex(ValueError,'real Windows loading'):public.validate_runtime(root,report,approval)
            report.write_text(json.dumps({'architecture':'Windows x64','export_check':'passed','exports':list(public.RUNTIME_EXPORTS),'sha256':v.sha(root/'SimConnect.dll')}), encoding='utf-8')
            with self.assertRaisesRegex(ValueError,'permission attestation'):public.validate_runtime(root,report,approval)

    def test_source_sha_dirty_branch_and_pinned_detached_worktree(self):
        with tempfile.TemporaryDirectory() as t:
            root=Path(t)
            def git(*args):return v.command(['git','-C',root,*args])
            git('init','-b','develop/v0.8.0');git('config','user.name','Synthetic fixture');git('config','user.email','fixture@example.invalid');git('config','commit.gpgsign','false')
            (root/'source.txt').write_text('original', encoding='utf-8');git('add','.');git('commit','-m','synthetic fixture');commit=git('rev-parse','HEAD')
            with mock.patch.object(v,'BASELINE',commit):
                self.assertEqual(commit,v.source_check(root,commit))
                with self.assertRaisesRegex(ValueError,'SHA mismatch'):v.source_check(root,'f'*40)
                (root/'source.txt').write_text('owner work', encoding='utf-8')
                with self.assertRaisesRegex(ValueError,'Dirty'):v.source_check(root,commit)
                self.assertEqual('owner work',(root/'source.txt').read_text(encoding='utf-8'))
                git('add','.');git('commit','-m','preserve fixture work');commit=git('rev-parse','HEAD');git('switch','-c','unrelated')
                with self.assertRaisesRegex(ValueError,'develop/v0.8.0'):v.source_check(root,commit)
                git('switch','--detach',commit);self.assertEqual(commit,v.source_check(root,commit))

    def test_pe_rejects_wrong_architecture_managed_wrapper_and_exe_dll_substitution(self):
        with tempfile.TemporaryDirectory() as t:
            p=Path(t)/'synthetic.dll';b=bytearray(512);b[:2]=b'MZ';struct.pack_into('<I',b,60,64);b[64:68]=b'PE\0\0';struct.pack_into('<H',b,68,0x8664);struct.pack_into('<H',b,86,0x2000);struct.pack_into('<H',b,88,0x20b);p.write_bytes(b)
            v.pe_x64(p,dll=True)
            with self.assertRaisesRegex(ValueError,'architecture'):v.pe_x64(p)
            struct.pack_into('<H',b,68,0x14c);p.write_bytes(b)
            with self.assertRaisesRegex(ValueError,'architecture'):v.pe_x64(p,dll=True)
            struct.pack_into('<H',b,68,0x8664);struct.pack_into('<I',b,88+112+14*8,1);p.write_bytes(b)
            with self.assertRaisesRegex(ValueError,'Managed'):v.pe_x64(p,dll=True)

    def test_unsigned_private_payload_cannot_authorize_public_packaging_or_synthetic_native_record(self):
        with tempfile.TemporaryDirectory() as t:
            root=Path(t);(root/'build-info.json').write_text(json.dumps({'profile':'validation','version':'0.8.0'}), encoding='utf-8')
            with self.assertRaisesRegex(ValueError,'not a production release'):public.validate_companion(root)
            (root/v.RECEIPT).write_text('{}', encoding='utf-8')
            with self.assertRaisesRegex(ValueError,'cannot become public'):public.validate_companion(root)
            with mock.patch.object(v.os,'name','posix'),self.assertRaisesRegex(ValueError,'actual Windows'):v.record(root,COMMIT,INSTANCE)
            with mock.patch.object(v,'command',return_value='AS-LIDAR-1 unexpected'),self.assertRaisesRegex(ValueError,'identity'):v.decoder_probe(Path('not-a-verified-helper'))

    def test_generated_form_is_not_passed_and_results_are_explicit_bound_append_only(self):
        template=v.acceptance_template(COMMIT,INSTANCE)
        self.assertTrue(all(r['outcome']=='Not Tested' for r in template['tests'].values()))
        with tempfile.TemporaryDirectory() as t:
            root=Path(t);app=root/'App';app.mkdir();(app/v.RECEIPT).write_text('SYNTHETIC-MOCK-ONLY', encoding='utf-8')
            receipt={'instance':INSTANCE,'catalog':{'catalogVersion':25},'files':{'SimConnect.dll':'a'*64,'LidarRuntime/AeroScience.LidarReader.exe':'b'*64,'Toolbar/sdk-build.json':'c'*64}}
            with mock.patch.object(v,'verify',return_value=receipt):
                with self.assertRaisesRegex(ValueError,'explicit observed'):v.observation(app,COMMIT,root/'Results','startup','Passed','no observed test',False)
                self.assertFalse((root/'Results').exists())
                first=v.observation(app,COMMIT,root/'Results','startup','Blocked','SYNTHETIC record test: no Windows PC',False)
                second=v.observation(app,COMMIT,root/'Results','startup','Not Tested','SYNTHETIC record test',False)
                self.assertNotEqual(first,second);self.assertEqual('Blocked',v.read(first)['outcome']);self.assertEqual(INSTANCE,v.read(second)['instance'])
                with self.assertRaisesRegex(ValueError,'outside'):v.observation(app,COMMIT,app/'Results','startup','Failed','fixture')
                with self.assertRaisesRegex(ValueError,'credentials'):v.observation(app,COMMIT,root/'Results','startup','Failed','password=private')

    def test_partial_changed_and_foreign_receipts_are_refused_before_any_launch(self):
        with tempfile.TemporaryDirectory() as t:
            app=Path(t)
            with self.assertRaises(FileNotFoundError):v.verify(app,COMMIT)
            receipt={'format_version':1,'profile':'validation','version':'0.8.0','source_commit':COMMIT,'instance':INSTANCE,'public_release_approved':False,'installed_acceptance':'Not Tested','files':{}}
            (app/v.RECEIPT).write_text(json.dumps(receipt), encoding='utf-8');(app/'partial.exe').write_bytes(b'partial')
            with self.assertRaisesRegex(ValueError,'bytes changed'):v.verify(app,COMMIT)
            with self.assertRaisesRegex(ValueError,'Stale/foreign'):v.verify(app,'b'*40)
            with self.assertRaisesRegex(ValueError,'Receipt hash'):v.verify(app,COMMIT,'f'*64)

    def test_profile_never_adopts_existing_unmarked_data_or_different_attempt(self):
        with tempfile.TemporaryDirectory() as t:
            local=Path(t);profile=local/'AeroScience-Validation-v0.8.0'/INSTANCE
            with mock.patch.object(v,'verify',return_value={'instance':INSTANCE}):
                self.assertEqual(str(profile),v.prepare_profile(Path('synthetic'),COMMIT,local))
                self.assertEqual(COMMIT,v.read(profile/'validation-profile.json')['source_commit'])
                (profile/'settings.json').write_text(json.dumps({'MissionDirectory':str(local/'Originals'),'ScienceLibraryDirectory':str(profile/'Science')}), encoding='utf-8')
                with self.assertRaisesRegex(ValueError,'escaped'):v.prepare_profile(Path('synthetic'),COMMIT,local)
                (profile/'settings.json').unlink();(profile/'validation-profile.json').unlink()
                with self.assertRaises(FileNotFoundError):v.prepare_profile(Path('synthetic'),COMMIT,local)

    def test_legacy_build_recipes_and_public_gate_are_unmodified(self):
        names=['scripts/release/Build-PrivateValidation.ps1','scripts/build/Publish-ValidationCompanion.ps1','scripts/build/Build-Toolbar.ps1','scripts/release/Build-ReleaseCandidate.ps1','scripts/release/package_release.py','scripts/build/lidar_runtime.py']
        for name in names:
            original=subprocess.check_output(['git','show',v.BASELINE+':'+name],cwd=ROOT)
            # Git may checkout text as CRLF; compare original source semantics.
            self.assertEqual(original.replace(b'\r\n',b'\n'),(ROOT/name).read_bytes().replace(b'\r\n',b'\n'),name)

    def test_powershell_entrypoints_parse_and_platform_refusal_creates_no_output(self):
        pwsh=shutil.which('pwsh')
        self.assertIsNotNone(pwsh,'Install PowerShell 7 for the mandatory maintainer script checks.')
        for path in [*ROOT.glob('scripts/build/*080*.ps1'),*ROOT.glob('scripts/release/*080*.ps1')]:
            script='$errors=$null;$tokens=$null;[void][Management.Automation.Language.Parser]::ParseFile($args[0],[ref]$tokens,[ref]$errors);if($errors){throw ($errors|Out-String)}'
            # A literal file argument is passed via an environment value to avoid PowerShell source quoting.
            script=script.replace('$args[0]','$env:P08B_PARSE_FILE')
            result=subprocess.run([pwsh,'-NoProfile','-Command',script],env={**os.environ,'P08B_PARSE_FILE':str(path)},capture_output=True,text=True,timeout=30)
            self.assertEqual(0,result.returncode,result.stderr)
        if os.name!='nt':
            with tempfile.TemporaryDirectory() as t:
                output=Path(t)/'Validation080-never-created'
                result=subprocess.run([pwsh,'-NoProfile','-File',str(ROOT/'scripts/release/Build-080PrivateValidation.ps1'),'-ExpectedSourceCommit',COMMIT,'-OutputDirectory',str(output)],capture_output=True,text=True,timeout=20)
                self.assertNotEqual(0,result.returncode);self.assertFalse(output.exists());self.assertIn('Windows x64',result.stderr)

class LicensePackagingTests(unittest.TestCase):
    def test_license_copy_preserves_publish_output_with_existing_or_absent_license_directory(self):
        pwsh=shutil.which('pwsh')
        self.assertIsNotNone(pwsh,'PowerShell 7 is required to exercise the actual packaging copy operation.')
        script=r"""
$ErrorActionPreference='Stop'
$errors=$null;$tokens=$null
$ast=[Management.Automation.Language.Parser]::ParseFile($env:P08B_COPY_SCRIPT,[ref]$tokens,[ref]$errors)
if($errors){throw ($errors|Out-String)}
$copies=@($ast.FindAll({param($node)
    $node -is [Management.Automation.Language.CommandAst] -and
    $node.GetCommandName() -eq 'Copy-Item' -and $node.Extent.Text.Contains("'licenses'")
},$true))
if($copies.Count -ne 1){throw 'Expected one actual root-license copy operation in the private builder.'}
$repo=$env:P08B_COPY_REPO
$app=$env:P08B_COPY_APP
& ([ScriptBlock]::Create($copies[0].Extent.Text))
"""
        for already_published in [False,True]:
            with self.subTest(already_published=already_published),tempfile.TemporaryDirectory(prefix='P08b copy [publish] ') as t:
                root=Path(t);repo=root/'source';app=root/'published'
                shutil.copytree(ROOT/'licenses',repo/'licenses')
                extra=repo/'licenses'/'nested [notices]'/'Aviso português.txt'
                extra.parent.mkdir();extra.write_text('Source notice: ciência; unchanged UTF-8 bytes.\n',encoding='utf-8')
                expected=v.inventory(repo/'licenses')
                app.mkdir();(app/'AeroScience.exe').write_bytes(b'PUBLISHED APP SENTINEL, NOT AN EXECUTABLE')
                (app/'LidarRuntime').mkdir();(app/'LidarRuntime/NOTICE.md').write_bytes(b'Published helper notice sentinel')
                if already_published:
                    (app/'licenses').mkdir()
                    first=next(p for p in sorted((repo/'licenses').iterdir()) if p.is_file())
                    shutil.copyfile(first,app/'licenses'/first.name)
                    (app/'licenses/published-only.txt').write_bytes(b'Additional published dependency notice')
                published=v.inventory(app)
                source=v.inventory(repo)
                result=subprocess.run([pwsh,'-NoProfile','-Command',script],env={**os.environ,
                    'P08B_COPY_SCRIPT':str(ROOT/'scripts/release/Build-080PrivateValidation.ps1'),
                    'P08B_COPY_REPO':str(repo),'P08B_COPY_APP':str(app)},capture_output=True,text=True,encoding='utf-8',timeout=30)
                self.assertEqual(0,result.returncode,result.stderr)
                actual=v.inventory(app)
                self.assertTrue(all(actual.get('licenses/'+name)==digest for name,digest in expected.items()),'Every canonical source notice must reach the single root licenses directory unchanged.')
                self.assertTrue(all(actual.get(name)==digest for name,digest in published.items()),'Published files and existing notices must be preserved.')
                self.assertFalse((app/'licenses/licenses').exists(),'The directory must be merged rather than nested.')
                self.assertEqual(source,v.inventory(repo),'Packaging must not edit original source notices.')

if __name__=='__main__':unittest.main()
