"""Release-control tests use explicit structural fixtures, never actual SDK/hardware evidence."""
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest
import zipfile
import test_packaging as fixtures

p = fixtures.packaging
COMMIT = 'a' * 40
DATE = '2026-10-17T00:00:00Z'


class ReleaseFlowTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix='aeroscience-release-test-')
        self.root = Path(self.temporary.name)
        self.published = self.root/'published'
        fixtures.PackagingTests().companion_fixture(self.published)
        (self.published/'SimConnect.dll').write_bytes(b'NOT A REAL RUNTIME: structural test only')
        self.sdk = self.root/'sdk-output'; self.sdk.mkdir()
        fixtures.PackagingTests().community_fixture(self.sdk, '1.9.18')
        self.receipt = self.root/'sdk-build.json'
        self.receipt.write_text(json.dumps(dict(variant='toolbar-full', source_commit=COMMIT, source_dirty=False,
            built_utc=DATE, builder_sha256='fixture-only', sdk_contract='passed', sdk_header_sha256='fixture-only',
            files={name: hashlib.sha256(path.read_bytes()).hexdigest() for name, path in p.files(self.sdk).items()})))
        (self.published/'build-info.json').write_text(json.dumps(dict(version=p.RELEASE_VERSION, source_commit=COMMIT, source_dirty=False, target='win-x64')))
        digest = hashlib.sha256((self.published/'SimConnect.dll').read_bytes()).hexdigest()
        self.report = self.root/'runtime-report.json'
        self.report.write_text(json.dumps(dict(checked_utc=DATE, architecture='Windows x64', export_check='passed',
            sha256=digest, exports=p.RUNTIME_EXPORTS, runtime_file=r'C:\Users\private\SDK\SimConnect.dll')))
        self.approval = self.root/'runtime-approval.json'
        self.approval.write_text(json.dumps(dict(format_version=2, redistribution_permitted=True, permission_for_file='SimConnect.dll',
            runtime_sha256=digest, permission_source='PRIVATE SENDER: fixture@example.invalid',
            permission_excerpt='PRIVATE ORIGINAL CORRESPONDENCE FIXTURE', private_evidence_reference='PRIVATE FILE PATH FIXTURE',
            permission_summary='TEST ONLY: maintainer attestation fixture, not a licence grant',
            conditions_reviewed=True, conditions_summary='No additional conditions in this structural fixture.', supporting_evidence_private=True,
            reviewed_by='fixture', reviewed_utc=DATE, private_extra_field='must not be published')))
        self.candidate = self.root/'candidates'/p.identity()['root_name']
        self.final = self.root/p.identity()['asset_name']
        self.acceptance = self.root/'acceptance.json'

    def tearDown(self):
        self.temporary.cleanup()

    def prepare(self):
        return p.prepare_candidate(self.published, self.sdk, self.receipt, self.report, self.approval, self.candidate)

    def accept(self, commit, digest):
        acceptance = p.acceptance_template(commit, digest)
        acceptance.update(tester='STRUCTURAL TEST FIXTURE ONLY', tested_utc=DATE)
        for name in p.ACCEPTANCE:
            acceptance['checks'][name].update(result='passed', evidence='UNIT FIXTURE, NOT WINDOWS ACCEPTANCE')
        self.acceptance.write_text(json.dumps(acceptance))
        return acceptance

    def test_science_release_is_a_prerelease_with_the_conventional_setup_filename(self):
        self.assertEqual(p.identity()['setup_name'], f"AeroScience-Setup-v{p.RELEASE_VERSION}.exe")
        self.assertTrue(p.identity()['prerelease'])
        self.assertFalse(p.identity('1.0.0')['prerelease'])

    def test_version_metadata_supports_future_prerelease_and_stable_versions(self):
        for version in ('0.1.1-alpha', '0.1.2-beta.2', '1.0.0-rc.1', '1.0.0'):
            meta = p.identity(version)
            self.assertEqual(meta['asset_name'], f'AeroScience-v{version}-win-x64.zip')
            self.assertEqual(meta['prerelease'], '-' in version)
        for invalid in ('v0.1.1-alpha', '../bad', '0.1', '1.0.0\n', '1.0.0+local'):
            with self.assertRaises(ValueError): p.identity(invalid)

    def test_science_prerelease_requires_installed_extended_and_scientific_checks(self):
        commit, digest = self.prepare(); data = self.accept(commit, digest)
        p.validate_acceptance(self.acceptance, source_commit=COMMIT, payload_sha256=digest)
        self.assertEqual(data['checks']['vr_performance']['result'], 'passed')
        self.assertEqual(data['checks']['science040_long_recording']['result'], 'passed')
        data['checks']['science040_long_recording']['result']='pending'
        self.acceptance.write_text(json.dumps(data))
        with self.assertRaisesRegex(ValueError,'science040_long_recording'):p.validate_acceptance(self.acceptance)
        data['checks']['science040_long_recording']['result']='passed'
        data['checks']['live_mission_smoke']['result'] = 'blocked'
        self.acceptance.write_text(json.dumps(data))
        with self.assertRaisesRegex(ValueError, 'live_mission_smoke'): p.finalize_candidate(self.candidate, self.acceptance, self.final)
        self.assertFalse(self.final.exists())

    def test_final_zip_keeps_exact_tested_payload_and_has_no_private_runtime_path(self):
        commit, digest = self.prepare(); self.accept(commit, digest)
        p.finalize_candidate(self.candidate, self.acceptance, self.final)
        info = p.verify_archive(self.final, self.final.with_suffix('.zip.sha256'))
        self.assertEqual(info['source_commit'], COMMIT); self.assertEqual(info['payload_sha256'], digest)
        self.assertIn('native-grid', p.release_notes(info)); self.assertIn('Windows/MSFS acceptance', p.release_notes(info))
        self.assertIn(p.identity()['asset_name'], p.release_notes(info))
        with zipfile.ZipFile(self.final) as archive:
            names = archive.namelist(); prefix = p.identity()['root_name']+'/'
            self.assertIn(prefix+'AeroScience.exe', names)
            self.assertIn(prefix+'Toolbar/aeroscience-toolbar/InGamePanels/aeroscience_panel.spb', names)
            self.assertIn(prefix+'Toolbar/sdk-build.json', names)
            self.assertNotIn(prefix+'RELEASE-STATUS.txt', names)
            self.assertFalse(any(n.endswith(('.ps1', '.cmd', '.pdb', '.db')) for n in names))
            report = json.loads(archive.read(prefix+'Dependencies/runtime-check.json'))
            approval = json.loads(archive.read(prefix+'Dependencies/runtime-approval.json'))
            self.assertNotIn('runtime_file', report); self.assertNotIn('private_extra_field', approval)
            self.assertNotIn('permission_excerpt', approval); self.assertNotIn('private_evidence_reference', approval)
            self.assertEqual(approval['format_version'], 2)
            self.assertEqual(approval['conditions_summary'], 'No additional conditions in this structural fixture.')
            self.assertEqual(approval['permission_source'], 'Project maintainer confirmation; supporting authorization retained privately.')
            for name in names:
                content = archive.read(name)
                self.assertNotIn(b'PRIVATE ORIGINAL CORRESPONDENCE FIXTURE', content)
                self.assertNotIn(b'fixture@example.invalid', content)
                self.assertNotIn(b'PRIVATE FILE PATH FIXTURE', content)
        with self.assertRaisesRegex(ValueError, 'overwrite'): p.finalize_candidate(self.candidate, self.acceptance, self.final)

    def test_tampered_candidate_or_wrong_source_cannot_use_old_acceptance(self):
        commit, digest = self.prepare(); data = self.accept(commit, digest)
        data['source_commit'] = 'b'*40; self.acceptance.write_text(json.dumps(data))
        with self.assertRaisesRegex(ValueError, 'different source'): p.finalize_candidate(self.candidate, self.acceptance, self.final)
        self.accept(commit, digest)
        (self.candidate/'AeroScience.dll').write_text('modified after testing')
        with self.assertRaisesRegex(ValueError, 'candidate payload'): p.finalize_candidate(self.candidate, self.acceptance, self.final)
        self.assertFalse(self.final.exists())

    def test_unconfirmed_permission_wrong_runtime_or_metadata_only_report_blocks_preparation(self):
        data = p.read_json(self.approval); data['redistribution_permitted'] = False
        self.approval.write_text(json.dumps(data))
        with self.assertRaisesRegex(ValueError, 'permission'): self.prepare()
        self.assertFalse(self.candidate.exists())
        data['redistribution_permitted'] = True; self.approval.write_text(json.dumps(data))
        report = p.read_json(self.report); report['export_check'] = 'not run'; self.report.write_text(json.dumps(report))
        with self.assertRaisesRegex(ValueError, 'real Windows'): self.prepare()
        report['export_check'] = 'passed'; report['sha256'] = '0'*64; self.report.write_text(json.dumps(report))
        with self.assertRaisesRegex(ValueError, 'matching'): self.prepare()

    def test_conditions_must_be_recorded_without_guessing(self):
        original = p.read_json(self.approval)
        for change in ({'conditions_reviewed': False}, {'conditions_summary': ''}, {'supporting_evidence_private': False}):
            with self.subTest(change=change):
                self.approval.write_text(json.dumps({**original, **change}))
                with self.assertRaisesRegex(ValueError, 'conditions'): self.prepare()
                self.assertFalse(self.candidate.exists())

    def test_correspondence_cannot_be_reintroduced_into_a_staged_public_record(self):
        commit, digest = self.prepare(); self.accept(commit, digest)
        public_path = self.candidate/'Dependencies/runtime-approval.json'
        record = p.read_json(public_path); record['permission_excerpt'] = 'PRIVATE ORIGINAL CORRESPONDENCE FIXTURE'
        public_path.write_text(json.dumps(record))
        with self.assertRaisesRegex(ValueError, 'non-public'): p.finalize_candidate(self.candidate, self.acceptance, self.final)
        self.assertFalse(self.final.exists())

    def test_email_archives_are_refused_in_distribution_inputs(self):
        for suffix in ('.eml', '.MSG', '.mbox'):
            with self.subTest(suffix=suffix):
                mail = self.published/('authorization'+suffix); mail.write_text('PRIVATE ORIGINAL CORRESPONDENCE FIXTURE')
                try:
                    with self.assertRaisesRegex(ValueError, 'correspondence'): self.prepare()
                    self.assertFalse(self.candidate.exists())
                finally: mail.unlink()

    def test_dirty_sdk_source_and_altered_sdk_assets_are_rejected(self):
        receipt = p.read_json(self.receipt); receipt['source_dirty'] = True; self.receipt.write_text(json.dumps(receipt))
        with self.assertRaisesRegex(ValueError, 'clean committed'): self.prepare()
        receipt['source_dirty'] = False; self.receipt.write_text(json.dumps(receipt))
        (self.sdk/'html_ui/InGamePanels/AeroScience/AeroScience.js').write_text('changed')
        with self.assertRaisesRegex(ValueError, 'size|differ'): self.prepare()

    def test_final_verification_rejects_checksum_changes_and_zip_traversal(self):
        commit, digest = self.prepare(); self.accept(commit, digest)
        p.finalize_candidate(self.candidate, self.acceptance, self.final)
        checksum = self.final.with_suffix('.zip.sha256')
        checksum.write_text('0'*64+'  '+self.final.name+'\n')
        with self.assertRaisesRegex(ValueError, 'checksum'): p.verify_archive(self.final, checksum)
        with zipfile.ZipFile(self.final, 'a') as archive: archive.writestr('../outside.txt', 'must not extract')
        p.write_checksum(self.final)
        with self.assertRaisesRegex(ValueError, 'Unsafe'): p.verify_archive(self.final, checksum)
        self.assertFalse((self.root/'outside.txt').exists())

    @unittest.skipUnless(shutil.which('pwsh'), 'PowerShell needed for local finalizer check')
    def test_local_finalizer_needs_no_runner_token_or_upload(self):
        import installer_fixtures
        candidate = installer_fixtures.candidate(self)
        output = self.root/'local release output'
        result = subprocess.run([shutil.which('pwsh'), '-NoProfile', '-File', str(p.REPO/'scripts/release/New-Release.ps1'),
            '-AcceptanceRecord', str(self.acceptance), '-CandidateFolder', str(candidate), '-OutputDirectory', str(output)],
            cwd=p.REPO, text=True, capture_output=True, timeout=60)
        self.assertEqual(result.returncode, 0, result.stdout+result.stderr)
        self.assertTrue((output/p.identity()['asset_name']).is_file())
        self.assertTrue((output/(p.identity()['asset_name']+'.sha256')).is_file())
        self.assertTrue((output/p.identity()['setup_name']).is_file())
        self.assertIn(COMMIT, (output/'RELEASE-NOTES.md').read_text(encoding='utf-8'))

    @unittest.skipUnless(shutil.which('pwsh'), 'PowerShell needed for optional publisher control-flow fixture')
    def test_optional_publisher_rerun_does_not_duplicate_or_replace_assets(self):
        import installer_fixtures
        candidate = installer_fixtures.candidate(self)
        installer_fixtures.installer.finalize(candidate, self.acceptance, self.root/'published release')
        self.final = self.root/'published release'/p.identity()['asset_name']
        harness = self.root/'publish-test.ps1'
        harness.write_text(r'''
param($Publisher, $Archive, $PythonExecutable, $Revision)
$global:mock = @{ release=$null; tag=$false; tagSha=$Revision; creates=0; uploads=0; updates=0 }
function global:python { & $PythonExecutable @args }
function global:git { $global:LASTEXITCODE=0; Write-Output $Revision }
function global:gh {
    $a = @($args); $global:LASTEXITCODE=0
    if ($a[0] -eq 'api') {
        $path=$a[1]
        if ($path -eq 'repos/JustSeanC/AeroScience') { '{"private":false,"full_name":"JustSeanC/AeroScience"}'; return }
        if ($path -like '*/git/ref/tags/*') {
            if ($global:mock.tag) { @{ object=@{ type='commit'; sha=$global:mock.tagSha } } | ConvertTo-Json -Depth 5 }
            else { $global:LASTEXITCODE=1; 'HTTP 404' }; return
        }
        if ($path -like '*/git/refs') { $global:mock.tag=$true; '{}'; return }
        if ($path -like '*/releases/tags/*') {
            if ($global:mock.release) { $global:mock.release | ConvertTo-Json -Depth 8 }
            else { $global:LASTEXITCODE=1; 'HTTP 404' }; return
        }
    }
    if ($a[0] -eq 'release' -and $a[1] -eq 'create') {
        $global:mock.creates++
        $global:mock.release=@{ draft=$true; prerelease=$true; assets=@(); html_url='https://github.com/JustSeanC/AeroScience/releases/tag/v0.1.1-alpha' }; return
    }
    if ($a[0] -eq 'release' -and $a[1] -eq 'upload') {
        $file=$a[3]; $global:mock.uploads++
        $global:mock.release.assets += @{ name=[IO.Path]::GetFileName($file); digest='sha256:'+(Get-FileHash $file -Algorithm SHA256).Hash.ToLowerInvariant() }; return
    }
    if ($a[0] -eq 'release' -and $a[1] -eq 'edit') { $global:mock.updates++; $global:mock.release.draft=$false; return }
    throw "Unmocked operation: $a"
}
function global:Invoke-WebRequest {
    param($Uri,$OutFile,$TimeoutSec)
    $source = Join-Path ([IO.Path]::GetDirectoryName($Archive)) ([Uri]$Uri).Segments[-1]
    Copy-Item -LiteralPath $source -Destination $OutFile
}
& $Publisher -Archive $Archive
& $Publisher -Archive $Archive
if ($global:mock.creates -ne 1 -or $global:mock.uploads -ne 3 -or $global:mock.updates -ne 1) { throw 'Rerun duplicated/replaced a release or asset.' }
$global:mock.tagSha='b'*40
try { & $Publisher -Archive $Archive; throw 'Wrong tag was accepted' }
catch { if ($_.Exception.Message -notlike '*different source revision*') { throw } }
$global:mock.tagSha=$Revision
$global:mock.release.assets[0].digest='sha256:'+('0'*64)
try { & $Publisher -Archive $Archive; throw 'Conflicting asset was accepted' }
catch { if ($_.Exception.Message -notlike '*Existing asset differs*') { throw } }
if ($global:mock.creates -ne 1 -or $global:mock.uploads -ne 3) { throw 'Conflict changed release assets' }
''')
        result = subprocess.run([shutil.which('pwsh'), '-NoProfile', '-File', str(harness),
            str(p.REPO/'scripts/release/Publish-Release.ps1'), str(self.final), sys.executable, COMMIT],
            cwd=p.REPO, text=True, capture_output=True, timeout=60)
        self.assertEqual(result.returncode, 0, result.stdout+result.stderr)
