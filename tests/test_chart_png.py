"""Synthetic adversarial checks for the independent PNG verifier, not original FAA acceptance."""
import importlib.util
from pathlib import Path
import struct
import tempfile
import unittest
import zlib

ROOT=Path(__file__).resolve().parents[1]
spec=importlib.util.spec_from_file_location('chart_png',ROOT/'tests/AeroScience.ReducedGisProbe/verify_chart_tiles.py')
png=importlib.util.module_from_spec(spec);spec.loader.exec_module(png)

def fixture(raw=None):
    def chunk(kind,data):return struct.pack('>I',len(data))+kind+data+struct.pack('>I',zlib.crc32(kind+data)&0xffffffff)
    raw=raw if raw is not None else (b'\0'+b'\x30\x80\xc0\xff'*256)*256
    return b'\x89PNG\r\n\x1a\n'+chunk(b'IHDR',struct.pack('>IIBBBBB',256,256,8,6,0,0,0))+chunk(b'IDAT',zlib.compress(raw))+chunk(b'IEND',b'')

class ChartPngTests(unittest.TestCase):
    def setUp(self):
        self.temp=tempfile.TemporaryDirectory();self.addCleanup(self.temp.cleanup);self.path=Path(self.temp.name)/'tile.png'
    def decode(self,data):self.path.write_bytes(data);return png.pixels(self.path)
    def test_actual_crc_and_bounded_rgba_pixel_stream(self):
        values=self.decode(fixture());self.assertEqual(256*256*4,len(values));self.assertEqual(b'\x30\x80\xc0\xff',values[-4:])
    def test_changed_crc_truncation_and_trailing_bytes_fail(self):
        bad=bytearray(fixture());bad[-1]^=1
        for data in [bad,fixture()[:-3],fixture()+b'trailing']:
            with self.subTest(size=len(data)):
                with self.assertRaises(ValueError):self.decode(data)
    def test_oversized_decompressed_stream_and_invalid_row_filter_fail(self):
        for raw in [b'\0'*(256*1025+1),(b'\1'+b'\0'*1024)*256]:
            with self.assertRaises(ValueError):self.decode(fixture(raw))
    def test_declared_oversized_chunk_is_rejected_before_allocation(self):
        bad=bytearray(fixture());struct.pack_into('>I',bad,8,0x7fffffff)
        with self.assertRaisesRegex(ValueError,'Unbounded'):self.decode(bad)

if __name__=='__main__':unittest.main()
