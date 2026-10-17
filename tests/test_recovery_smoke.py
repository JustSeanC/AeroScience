"""Recovery launch checks; the actual-process smoke separately tests database recovery."""
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest


REPO = Path(__file__).resolve().parents[1]


class RecoveryLaunchTests(unittest.TestCase):
    def test_missing_selected_host_fails_without_falling_back_to_another_build(self):
        with tempfile.TemporaryDirectory(prefix='aeroscience recovery path ') as work:
            selected = Path(work)/'x64 output'/'AeroScience.TestHost.dll'
            result = subprocess.run([sys.executable, str(REPO/'scripts/build/smoke_recovery.py'),
                                     '--host-dll', str(selected)], cwd=REPO, capture_output=True, text=True, timeout=10)
            self.assertEqual(result.returncode, 2, result.stdout+result.stderr)
            self.assertIn('Recovery test-host DLL does not exist:', result.stderr)
            self.assertIn(str(selected.resolve()), result.stderr)
            self.assertFalse(selected.parent.exists())


if __name__ == '__main__': unittest.main()
