"""Real cmd.exe regression for the Windows source-built decoder bootstrap."""
import importlib.util
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

ROOT=Path(__file__).resolve().parents[1]
SPEC=importlib.util.spec_from_file_location('lidar_runtime',ROOT/'scripts/build/lidar_runtime.py')
RUNTIME=importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(RUNTIME)

class LidarRuntimeTests(unittest.TestCase):
    @unittest.skipUnless(os.name=='nt','Actual Windows cmd.exe execution; native Linux decoding is tested separately.')
    def test_installed_compiler_batch_with_spaces_runs_and_retains_x64_environment(self):
        with tempfile.TemporaryDirectory(prefix='aeroscience lidar compiler ') as temporary:
            batch=Path(temporary)/'VS build tools with spaces.cmd'
            batch.write_text('@echo off\nset "AS_LIDAR_FIXTURE_VALUE=value with spaces"\nset "AS_LIDAR_FIXTURE_ARCH=%~2"\nset "Path=%~dp0;%Path%"\nexit /b 0\n')
            values=RUNTIME.msvc_environment(batch)
            self.assertEqual('value with spaces',values['AS_LIDAR_FIXTURE_VALUE'])
            self.assertEqual('-arch=x64',values['AS_LIDAR_FIXTURE_ARCH'])
            self.assertNotIn('AS_LIDAR_FIXTURE_VALUE',os.environ)
            self.assertEqual(1,len([key for key in values if key.upper()=='PATH']))
            # A real OS executable with a compiler name exists only in the child
            # PATH. It must launch by its resolved absolute path, not parent PATH.
            executable=Path(temporary)/'cl.exe'
            shutil.copyfile(Path(os.environ['SystemRoot'])/'System32/cmd.exe',executable)
            compiler=RUNTIME.msvc_compiler(values)
            self.assertEqual(executable,Path(compiler))
            output=subprocess.check_output([compiler,'/d','/c','echo','AS_LIDAR_COMPILER_LOOKUP'],env=values,text=True)
            self.assertEqual('AS_LIDAR_COMPILER_LOOKUP',output.strip())

    def test_unsupported_batch_path_is_refused_before_invocation(self):
        for value in ['C:/VS%UNTRUSTED%/Tools.bat','C:/VS"/Tools.bat','C:/VS\n/Tools.bat']:
            with self.subTest(value=value),self.assertRaises(ValueError):RUNTIME.msvc_environment(value)

if __name__=='__main__':unittest.main()
