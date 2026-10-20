"""Run the actual PowerShell preflight with simulated compiler processes, not Windows installers."""
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

REPO = Path(__file__).resolve().parents[1]
PWSH = os.environ.get('PWSH') or shutil.which('pwsh')


@unittest.skipUnless(PWSH, 'PowerShell 7 is required for Inno preflight checks')
class InnoPreflightTests(unittest.TestCase):
    def invoke(self, root, output, exit_code=0):
        # This unversioned PowerShell process stands in for ISCC.exe's launcher.
        # Its stdout models the official compiler engine report, never SDK/setup evidence.
        compiler = root/'Compiler with spaces'/'compiler.ps1'
        compiler.parent.mkdir()
        compiler.write_text('''
if ($args.Count -ne 2 -or $args[0] -ne '/O-') { throw 'Expected a no-output compile.' }
$probe = $args[1]
$source = Get-Content -LiteralPath $probe -Raw
if ($source -notmatch 'Output=no' -or $source -notmatch 'CreateAppDir=no' -or $source -notmatch 'Uninstallable=no') {
    throw 'Probe must not create an installation or application output.'
}
Set-Content -LiteralPath (Join-Path $PSScriptRoot 'probe-path.txt') -Value $probe
''' + "Write-Output '" + output.replace("'", "''") + "'\nexit " + str(exit_code) + '\n', encoding='utf-8')
        harness = root/'test.ps1'
        harness.write_text('''
param($Helper, $Compiler)
$ErrorActionPreference = 'Stop'
. $Helper
$selected = Resolve-InnoSetupCompiler -InnoCompiler $Compiler
$version = Get-InnoSetupCompilerVersion -CompilerPath $selected
Write-Output ("ENGINE=" + $version.ToString())
''', encoding='utf-8')
        result = subprocess.run([PWSH, '-NoLogo', '-NoProfile', '-File', str(harness),
                                 str(REPO/'scripts/release/InnoSetup.ps1'), str(compiler)],
                                text=True, capture_output=True, timeout=30)
        probe = Path((compiler.parent/'probe-path.txt').read_text(encoding='utf-8-sig').strip())
        self.assertFalse(probe.parent.exists(), 'Temporary probe files must be removed on success and failure.')
        return result

    def test_supported_engine_versions_with_unversioned_launcher(self):
        for version in ('6.3.0', '6.7.0', '7.0.0'):
            with self.subTest(version=version), tempfile.TemporaryDirectory(prefix='aeroscience inno ') as directory:
                result = self.invoke(Path(directory), 'Compiler engine version: Inno Setup ' + version)
                self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
                self.assertIn('ENGINE=' + version, result.stdout)

    def test_old_engine_is_refused(self):
        with tempfile.TemporaryDirectory() as directory:
            result = self.invoke(Path(directory), 'Compiler engine version: Inno Setup 6.2.2')
            self.assertNotEqual(result.returncode, 0)
            self.assertIn('6.3 or newer is required', result.stderr)
            self.assertIn('6.2.2', result.stderr)

    def test_unknown_engine_or_failed_compile_cannot_pass(self):
        cases = [('Inno Setup 6 Command-Line Compiler', 0, 'Cannot identify'),
                 ('Compiler engine version: Inno Setup 6.7.0', 2, 'preflight failed')]
        for output, exit_code, expected in cases:
            with self.subTest(output=output, exit_code=exit_code), tempfile.TemporaryDirectory() as directory:
                result = self.invoke(Path(directory), output, exit_code)
                self.assertNotEqual(result.returncode, 0)
                self.assertIn(expected, result.stderr)


if __name__ == '__main__': unittest.main()
