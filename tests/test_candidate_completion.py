"""Execute the build script's completion branch; no SDK/installer/hardware pass is implied."""
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
PWSH = shutil.which('pwsh')


@unittest.skipUnless(PWSH, 'PowerShell needed for release-completion control flow')
class CandidateCompletionTests(unittest.TestCase):
    def complete(self, version, prepare_only=False):
        with tempfile.TemporaryDirectory(prefix='aeroscience completion fixture ') as temporary:
            fixture = Path(temporary)
            finalizer = fixture / 'scripts/release/New-Release.ps1'
            finalizer.parent.mkdir(parents=True)
            finalizer.write_text('''param([string]$OutputDirectory)
[IO.File]::WriteAllText((Join-Path $OutputDirectory 'automatic-finalizer.called'), 'CONTROL FIXTURE ONLY')
''', encoding='utf-8')
            harness = fixture / 'complete.ps1'
            harness.write_text(r'''
param($BuildScript, $FixtureRoot, $ReleaseVersion, $PrepareFlag)
$ErrorActionPreference = 'Stop'
$parseErrors = $null
$tree = [Management.Automation.Language.Parser]::ParseFile($BuildScript, [ref]$null, [ref]$parseErrors)
if ($parseErrors.Count) { throw 'Build script failed to parse.' }
# Execute the actual production completion branch, without running Windows/SDK
# preparation. A fake finalizer records whether this branch invokes it.
$branches = @($tree.FindAll({ param($node)
    $node -is [Management.Automation.Language.IfStatementAst] -and $node.Extent.Text -match 'New-Release\.ps1'
}, $true))
if ($branches.Count -ne 1) { throw 'Expected one release completion branch.' }
$metadata = [pscustomobject]@{ version=$ReleaseVersion; package_version=$ReleaseVersion.Split('-')[0]; setup_name="AeroScience-Setup-v$ReleaseVersion.exe" }
$PrepareOnly = $PrepareFlag -eq 'yes'
$repo = $FixtureRoot
$OutputDirectory = $FixtureRoot
$revision = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'
& ([scriptblock]::Create($branches[0].Extent.Text))
''', encoding='utf-8')
            result = subprocess.run([PWSH, '-NoProfile', '-File', str(harness),
                                     str(ROOT / 'scripts/release/Build-ReleaseCandidate.ps1'),
                                     str(fixture), version, 'yes' if prepare_only else 'no'],
                                    text=True, capture_output=True, timeout=30)
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
            return (fixture / 'automatic-finalizer.called').exists(), result.stdout

    def test_scientific_candidates_stop_successfully_before_automatic_finalization(self):
        for version in ('0.4.0', '0.4.0-beta.1', '0.4.1', '1.0.0'):
            with self.subTest(version=version):
                invoked, output = self.complete(version)
                self.assertFalse(invoked, 'A scientific candidate must not call the automatic finalizer.')
                self.assertIn('Candidate ready for actual Windows/MSFS testing', output)
                self.assertIn('Complete-ReleaseAcceptance.ps1', output)
                self.assertIn('do not rebuild', output)

    def test_legacy_alpha_keeps_its_existing_automatic_technical_finalization(self):
        invoked, output = self.complete('0.3.2')
        self.assertTrue(invoked)
        self.assertIn('Hardware acceptance: Not verified', output)

    def test_prepare_only_never_invokes_the_finalizer(self):
        for version in ('0.3.2', '0.4.0'):
            with self.subTest(version=version):
                invoked, _ = self.complete(version, prepare_only=True)
                self.assertFalse(invoked)


if __name__ == '__main__':
    unittest.main()
