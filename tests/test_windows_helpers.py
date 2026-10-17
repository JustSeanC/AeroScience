"""PowerShell helper checks; portable tests do not establish Windows/MSFS operation."""
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest
import zipfile

REPO = Path(__file__).resolve().parents[1]
PWSH = os.environ.get('PWSH') or shutil.which('pwsh')


@unittest.skipUnless(PWSH, 'PowerShell 7 is required for helper script checks')
class WindowsHelperTests(unittest.TestCase):
    def invoke(self, script, *args):
        return subprocess.run([PWSH, '-NoLogo', '-NoProfile', '-File', str(script), *map(str, args)],
                              text=True, capture_output=True, timeout=30)

    def test_all_scripts_parse(self):
        command = r'''
          $bad = 0
          Get-ChildItem scripts -Filter *.ps1 -Recurse | ForEach-Object {
            $tokens = $null; $errors = $null
            [Management.Automation.Language.Parser]::ParseFile($_.FullName, [ref]$tokens, [ref]$errors) | Out-Null
            $bad += $errors.Count
          }
          if ($bad) { exit 1 }
        '''
        result = subprocess.run([PWSH, '-NoLogo', '-NoProfile', '-Command', command], cwd=REPO, capture_output=True, text=True, timeout=30)
        self.assertEqual(result.returncode, 0, result.stderr)

    def test_feedback_excludes_database_credentials_and_sdk_binaries(self):
        with tempfile.TemporaryDirectory(prefix='aeroscience feedback ') as temp:
            root = Path(temp); data = root/'data'; (data/'logs').mkdir(parents=True)
            (data/'logs/engine-20261016.log').write_text('Test connection failure HRESULT 0x80004005\n')
            (data/'missions.db').write_text('private recording fixture')
            (data/'credentials.env').write_text('private fixture')
            build = root/'build'; build.mkdir(); (build/'build.log').write_text('SDK build test fixture\n')
            (build/'SimConnect.dll').write_bytes(b'proprietary fixture excluded')
            output = root/'reports'
            result = self.invoke(REPO/'scripts/release/Collect-Feedback.ps1', '-DataDirectory', data,
                                 '-OutputDirectory', output, '-BuildLogDirectory', build)
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
            archives = list(output.glob('*.zip')); self.assertEqual(len(archives), 1)
            with zipfile.ZipFile(archives[0]) as archive:
                names = archive.namelist()
                self.assertTrue(any(name.endswith('/logs/engine-20261016.log') for name in names))
                self.assertTrue(any(name.endswith('/build/build.log') for name in names))
                self.assertTrue(any(name.endswith('/FEEDBACK.txt') for name in names))
                self.assertFalse(any(name.endswith(('.db', '.env', '.dll')) for name in names))
            self.assertEqual((data/'missions.db').read_text(), 'private recording fixture')

    def test_runtime_checker_rejects_non_pe_files(self):
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp)/'SimConnect.dll'; path.write_bytes(b'not a DLL')
            result = self.invoke(REPO/'scripts/build/Test-SimConnectRuntime.ps1', '-RuntimePath', path, '-MetadataOnly')
            self.assertNotEqual(result.returncode, 0)
            self.assertIn('Not a Windows PE image', result.stderr)

    def test_approval_helper_records_actual_hash_and_public_summary_without_correspondence(self):
        # This helper records attestation/hash only. These bytes are not a real runtime.
        import hashlib
        with tempfile.TemporaryDirectory(prefix='aeroscience approval ') as temp:
            root = Path(temp); runtime = root/'SimConnect.dll'; runtime.write_bytes(b'APPROVAL HASH FIXTURE ONLY')
            (root/'private-original.eml').write_text('PRIVATE CORRESPONDENCE MUST STAY HERE')
            output = root/'release records/runtime-approval.json'
            script = REPO/'scripts/release/New-RuntimeApproval.ps1'
            args = ('-RuntimePath', runtime, '-OutputPath', output, '-ConditionsSummary', 'No additional conditions reported by the maintainer.')
            rejected = self.invoke(script, *args)
            self.assertNotEqual(rejected.returncode, 0); self.assertFalse(output.exists())
            result = self.invoke(script, *args, '-RedistributionAuthorized')
            self.assertEqual(result.returncode, 0, result.stdout+result.stderr)
            data = json.loads(output.read_text(encoding='utf-8-sig'))
            self.assertEqual(data['runtime_sha256'], hashlib.sha256(runtime.read_bytes()).hexdigest())
            self.assertEqual(data['format_version'], 2); self.assertTrue(data['redistribution_permitted'])
            self.assertTrue(data['conditions_reviewed']); self.assertTrue(data['supporting_evidence_private'])
            self.assertEqual(data['reviewed_by'], 'AeroScience maintainer'); self.assertIn('T', data['reviewed_utc'])
            self.assertNotIn('permission_excerpt', data); self.assertNotIn('PRIVATE CORRESPONDENCE', output.read_text())
            original = output.read_bytes()
            duplicate = self.invoke(script, *args, '-RedistributionAuthorized')
            self.assertNotEqual(duplicate.returncode, 0); self.assertEqual(output.read_bytes(), original)

    def test_pe_metadata_check_does_not_claim_runtime_exports_or_simulator_connection(self):
        # A real self-contained publish's native runtime is a useful PE fixture, not a SimConnect DLL.
        native = REPO/'artifacts/Windows-Companion/coreclr.dll'
        if not native.exists(): self.skipTest('Publish the Windows companion first for the native PE fixture')
        with tempfile.TemporaryDirectory() as temp:
            report = Path(temp)/'report.json'
            result = self.invoke(REPO/'scripts/build/Test-SimConnectRuntime.ps1', '-RuntimePath', native, '-MetadataOnly', '-ReportPath', report)
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
            data = json.loads(report.read_text(encoding='utf-8-sig'))
            self.assertEqual(data['export_check'], 'not run')
            self.assertEqual(data['simulator_connection'], 'not tested')

    def test_failed_sdk_contract_retains_log_and_cannot_issue_build_receipt(self):
        with tempfile.TemporaryDirectory(prefix='aeroscience sdk failure ') as temp:
            root = Path(temp); scripts = root/'scripts/build'; scripts.mkdir(parents=True)
            shutil.copy2(REPO/'scripts/build/Build-Toolbar.ps1', scripts/'Build-Toolbar.ps1')
            shutil.copy2(REPO/'scripts/build/sdk-contract.cpp', scripts/'sdk-contract.cpp')
            sdk = root/'SDK with spaces'; (sdk/'Tools/bin').mkdir(parents=True); (sdk/'SimConnect SDK/include').mkdir(parents=True)
            (sdk/'Tools/bin/fspackagetool.exe').write_bytes(b'not invoked')
            (sdk/'SimConnect SDK/include/SimConnect.h').write_text('// not compiled by the mocked compiler')
            harness = root/'test.ps1'
            harness.write_text('''
param($BuildScript, $SdkPath)
function global:cl.exe { Write-Output 'MOCK: intentional compiler failure'; $global:LASTEXITCODE = 17 }
function global:python { $global:LASTEXITCODE = 0 }
try { & $BuildScript -SdkRoot $SdkPath; throw 'Build unexpectedly succeeded' }
catch {
 if ($_.Exception.Message -notlike '*Installed SDK differs*') { throw }
 if ($global:LASTEXITCODE -ne 17) { throw 'Compiler exit status lost' }
}
''')
            result = self.invoke(harness, scripts/'Build-Toolbar.ps1', sdk)
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
            stage = root/'artifacts/toolbar-full'
            self.assertIn('intentional compiler failure', (stage/'sdk-contract.log').read_text(encoding='utf-8-sig'))
            self.assertFalse((stage/'sdk-build.json').exists())

    def test_conflicting_probe_flags_preserve_existing_generated_output(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp); scripts = root/'scripts/build'; scripts.mkdir(parents=True)
            shutil.copy2(REPO/'scripts/build/Build-Toolbar.ps1', scripts/'Build-Toolbar.ps1')
            previous = root/'artifacts/toolbar-poc/sdk-build.json'
            previous.parent.mkdir(parents=True); previous.write_text('existing build receipt fixture')
            result = self.invoke(scripts/'Build-Toolbar.ps1', '-SdkRoot', root/'missing SDK',
                                 '-ProofOfConcept', '-WindowProbe')
            self.assertNotEqual(result.returncode, 0)
            self.assertIn('Choose either', result.stderr)
            self.assertEqual(previous.read_text(), 'existing build receipt fixture')


if __name__ == '__main__': unittest.main()
