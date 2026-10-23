"""Pinned native recipe security tests; synthetic build roots, no downloaded or executed DLLs."""
from pathlib import Path
import hashlib
import json
import subprocess
import sys
import tempfile
import unittest

ROOT=Path(__file__).resolve().parents[1]
PROBE=ROOT/'tests/AeroScience.ReducedGisProbe'

class ReducedGisRecipeTests(unittest.TestCase):
    def setUp(self):
        self.temp=tempfile.TemporaryDirectory();self.addCleanup(self.temp.cleanup);self.root=Path(self.temp.name)
        self.gdal=self.root/'ports/gdal/portfile.cmake';self.jpeg=self.root/'ports/libjpeg-turbo/portfile.cmake'
        for path,fixture in [(self.gdal,'gdal-port.cmake'),(self.jpeg,'jpeg-port.cmake')]:
            path.parent.mkdir(parents=True);path.write_bytes((PROBE/'fixtures'/fixture).read_bytes())
    def prepare(self):
        return subprocess.run([sys.executable,str(PROBE/'prepare_port.py'),'--vcpkg',str(self.root),'--receipt',str(self.root/'receipt.json')],capture_output=True,text=True)
    def test_pinned_recipe_preserves_versions_and_guards_absent_optional_turbojpeg(self):
        result=self.prepare();self.assertEqual(0,result.returncode,result.stderr)
        receipt=json.loads((self.root/'receipt.json').read_text())
        self.assertEqual('3.12.4',receipt['gdalVersion']);self.assertFalse(receipt['turbojpegEnabled'])
        self.assertEqual('static',receipt['jpegLibraryLinkage'])
        for folder in ('lib','debug/lib'):
            self.assertIn(f'if(EXISTS "${{CURRENT_PACKAGES_DIR}}/{folder}/turbojpeg-static.lib")',self.jpeg.read_text())
        self.assertEqual(hashlib.sha256(self.jpeg.read_bytes()).hexdigest(),receipt['jpegReducedPortSha256'])
        self.assertIn('SHA512 1ce063e9',self.jpeg.read_text());self.assertIn('-DGDAL_ENABLE_DRIVER_GTIFF=ON',self.gdal.read_text())
        second=self.prepare();self.assertNotEqual(0,second.returncode)
    def test_unreviewed_either_port_does_not_partially_mutate_the_other(self):
        for path in [self.gdal,self.jpeg]:
            with self.subTest(port=path.parent.name):
                original=path.read_bytes();path.write_bytes(original+b'\n# unreviewed alteration\n')
                before=[self.gdal.read_bytes(),self.jpeg.read_bytes()]
                self.assertNotEqual(0,self.prepare().returncode)
                self.assertEqual(before,[self.gdal.read_bytes(),self.jpeg.read_bytes()]);path.write_bytes(original)
    def test_crlf_checkouts_use_the_same_canonical_pinned_recipes(self):
        for path in [self.gdal,self.jpeg]:path.write_bytes(path.read_bytes().replace(b'\r\n',b'\n').replace(b'\n',b'\r\n'))
        result=self.prepare();self.assertEqual(0,result.returncode,result.stderr)
        self.assertNotIn(b'\r',self.gdal.read_bytes());self.assertNotIn(b'\r',self.jpeg.read_bytes())

if __name__=='__main__':unittest.main()
