import importlib.util
from pathlib import Path
import unittest

ROOT=Path(__file__).resolve().parents[1]
spec=importlib.util.spec_from_file_location('tfr_operations',ROOT/'scripts/research/verify_tfr_operations.py')
module=importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)

class TfrOriginalTests(unittest.TestCase):
    def test_public_originals_have_exact_receipt_bytes_on_every_checkout(self):
        records=module.verify_originals(ROOT/'tests/AeroScience.Tests/TfrOriginals')
        self.assertEqual(11,len(records))
        self.assertEqual(8,sum('/download/detail_' in r['url'] for r in records))

if __name__=='__main__':unittest.main()
