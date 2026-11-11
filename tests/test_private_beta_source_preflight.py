"""Execute both private builders' source guards with real Git and PowerShell.

Only the source-selection statements are executed; no platform guard, SDK build,
runtime approval or installer acceptance is simulated.
"""
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
PWSH = shutil.which('pwsh')
BRANCH = 'build/v0.7.1-beta2'
SCRIPTS = ('Build-BetaInstaller.ps1', 'Build-PrivateValidation.ps1')
DRIVER = r'''
param($Builder, $Checkout, $ExpectedSourceCommit)
$ErrorActionPreference = 'Stop'
$tokens=$null; $errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile($Builder,[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Builder does not parse.'}
$body=$ast.Find({param($node) $node -is [Management.Automation.Language.TryStatementAst]},$false).Body
$assignments=@($body.Statements | Where-Object {
    $_ -is [Management.Automation.Language.AssignmentStatementAst] -and
    $_.Left.VariablePath.UserPath -in @('revision','branch','sourceSelected')
})
$guard=@($body.Statements | Where-Object {
    $_ -is [Management.Automation.Language.IfStatementAst] -and
    $_.Extent.Text.Contains('-not $sourceSelected')
})
if($assignments.Count -ne 3 -or $guard.Count -ne 1){throw 'Source guard was not located.'}
$identity=@{branch='build/v0.7.1-beta2'}
$PrivateBetaVersion='0.7.1-beta.2'
Push-Location $Checkout
try {
    & ([scriptblock]::Create((@($assignments + $guard) | ForEach-Object {$_.Extent.Text}) -join "`n"))
    Write-Output 'SOURCE-CHECK-PASSED'
} finally {Pop-Location}
'''


@unittest.skipUnless(PWSH, 'PowerShell 7 is required for private source preflight checks')
class PrivateBetaSourcePreflightTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory(prefix='aeroscience source preflight ')
        self.addCleanup(temporary.cleanup)
        root = Path(temporary.name)
        self.checkout = root/'checkout'
        self.checkout.mkdir()
        self.git('init', '--quiet', '--initial-branch='+BRANCH)
        (self.checkout/'sentinel.txt').write_text('preserve original\n')
        self.git('add', 'sentinel.txt')
        self.git('-c', 'user.name=Fixture', '-c', 'user.email=fixture@example.invalid',
                 'commit', '--quiet', '-m', 'source fixture')
        self.revision = self.git('rev-parse', 'HEAD').strip()
        self.driver = root/'source-check.ps1'
        self.driver.write_text(DRIVER)

    def git(self, *args):
        return subprocess.run(['git', *args], cwd=self.checkout, check=True,
                              capture_output=True, text=True).stdout

    def check_builders(self, expected, accepted):
        for name in SCRIPTS:
            with self.subTest(builder=name, expected=expected):
                args = [PWSH, '-NoProfile', '-File', str(self.driver),
                        '-Builder', str(ROOT/'scripts/release'/name),
                        '-Checkout', str(self.checkout)]
                if expected is not None:
                    args += ['-ExpectedSourceCommit', expected]
                result = subprocess.run(args, capture_output=True, text=True, timeout=30)
                output = result.stdout+result.stderr
                if accepted:
                    self.assertEqual(0, result.returncode, output)
                    self.assertIn('SOURCE-CHECK-PASSED', output)
                else:
                    self.assertNotEqual(0, result.returncode, output)
                    self.assertIn('Use clean '+BRANCH+' source', output)
                self.assertNotIn('null-valued expression', output)

    def test_clean_named_branch_and_exactly_pinned_detached_head_are_accepted(self):
        self.check_builders(None, accepted=True)
        self.check_builders(self.revision, accepted=True)
        self.git('switch', '--quiet', '--detach', self.revision)
        self.check_builders(self.revision, accepted=True)

    def test_detached_head_without_matching_explicit_pin_is_refused(self):
        self.git('switch', '--quiet', '--detach', self.revision)
        self.check_builders(None, accepted=False)
        self.check_builders('0'*40, accepted=False)

    def test_dirty_pinned_detached_head_is_refused_without_changing_files(self):
        self.git('switch', '--quiet', '--detach', self.revision)
        sentinel = self.checkout/'sentinel.txt'
        sentinel.write_text('preserve edited user file\n')
        self.check_builders(self.revision, accepted=False)
        self.assertEqual('preserve edited user file\n', sentinel.read_text())

    def test_unrelated_named_branch_is_refused_even_with_matching_pin(self):
        self.git('switch', '--quiet', '--create', 'unrelated-branch')
        self.check_builders(self.revision, accepted=False)


if __name__ == '__main__':
    unittest.main()
