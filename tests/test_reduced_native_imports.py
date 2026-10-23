"""Synthetic PE import/security regressions; never native execution or redistribution acceptance."""
import importlib.util
from pathlib import Path
import struct
import tempfile
import unittest

ROOT=Path(__file__).resolve().parents[1]
spec=importlib.util.spec_from_file_location('native_import_audit',ROOT/'tests/AeroScience.ReducedGisProbe/inspect_native.py')
audit=importlib.util.module_from_spec(spec)
spec.loader.exec_module(audit)


def fixture(required='kernel32.dll',delay=None):
    data=bytearray(4096)
    def u16(offset,value):struct.pack_into('<H',data,offset,value)
    def u32(offset,value):struct.pack_into('<I',data,offset,value)
    data[:2]=b'MZ';u32(0x3c,0x80);data[0x80:0x84]=b'PE\0\0';u16(0x84,0x8664);u16(0x86,1);u16(0x94,240)
    optional=0x98;u16(optional,0x20b);section=optional+240;u32(section+12,0x1000);u32(section+16,3584);u32(section+20,512)
    u32(optional+120,0x1000);u32(512+12,0x1100);encoded=required.encode()+b'\0';data[768:768+len(encoded)]=encoded
    if delay:
        u32(optional+112+13*8,0x1200);u32(1024,1);u32(1028,0x1300);encoded=delay.encode()+b'\0';data[1280:1280+len(encoded)]=encoded
    return data


class ReducedNativeImportTests(unittest.TestCase):
    def setUp(self):
        self.temporary=tempfile.TemporaryDirectory();self.addCleanup(self.temporary.cleanup);self.folder=Path(self.temporary.name)
    def write(self,content,name='gdal.dll'):
        path=self.folder/name;path.write_bytes(content);return path
    def test_x64_direct_and_delay_imports_are_both_accounted_for(self):
        path=self.write(fixture(delay='crypt32.dll'))
        self.assertEqual(['crypt32.dll','kernel32.dll'],audit.imports(path))
        self.assertTrue(audit.verify(self.folder)['importsAccountedFor'])
    def test_owned_transitive_library_is_allowed_but_unknown_driver_is_rejected(self):
        self.write(fixture('geotiff.dll'));self.write(fixture(),'geotiff.dll');self.assertTrue(audit.verify(self.folder)['importsAccountedFor'])
        self.write(fixture('unreviewed-driver.dll'))
        with self.assertRaisesRegex(ValueError,'Unreviewed/unbundled'):audit.verify(self.folder)
    def test_dynamic_crt_cannot_hide_in_direct_or_delay_imports(self):
        for direct,delay in [('vcruntime140.dll',None),('kernel32.dll','msvcp140.dll')]:
            with self.subTest(direct=direct,delay=delay):
                self.write(fixture(direct,delay))
                with self.assertRaisesRegex(ValueError,'VC runtime'):audit.verify(self.folder)
    def test_failed_audit_retains_all_component_identities_and_direct_delay_imports(self):
        self.write(fixture('vcruntime140.dll'),'gdal.dll')
        self.write(fixture(delay='msvcp140.dll'),'proj_9.dll')
        result=audit.inspect(self.folder)
        self.assertFalse(result['importsAccountedFor']);self.assertEqual(2,len(result['errors']))
        self.assertIn('gdal.dll: Unshipped VC runtime',result['errors'][0])
        self.assertEqual(['msvcp140.dll'],result['files'][1]['delayImports'])
        self.assertEqual(['vcruntime140.dll'],result['files'][0]['directImports'])
        self.assertEqual(64,len(result['files'][1]['sha256']))
    def test_non_x64_architecture_is_rejected(self):
        data=fixture();struct.pack_into('<H',data,0x84,0x14c)
        with self.assertRaises(AssertionError):audit.imports(self.write(data))
    def test_unmapped_rva_cannot_be_interpreted_as_a_dependency(self):
        data=fixture();struct.pack_into('<I',data,512+12,0x7fffffff)
        with self.assertRaisesRegex(ValueError,'Unmapped'):audit.imports(self.write(data))
    def test_unsupported_delay_addressing_cannot_silently_skip_imports(self):
        data=fixture(delay='kernel32.dll');struct.pack_into('<I',data,1024,0)
        with self.assertRaisesRegex(ValueError,'delay-import'):audit.imports(self.write(data))
    def test_file_size_bound_is_checked_before_loading_bytes(self):
        path=self.folder/'oversized.dll'
        with path.open('wb') as stream:stream.truncate(33554433)
        with self.assertRaisesRegex(ValueError,'size/link'):audit.imports(path)


if __name__=='__main__':unittest.main()
