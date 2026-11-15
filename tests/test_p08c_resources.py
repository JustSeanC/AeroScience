"""Resource harness tests are engineering fixtures, not dataset benchmarks."""
import importlib.util
from pathlib import Path
import sys
import tempfile
import unittest
from unittest import mock

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT/'scripts/validation'))
import p08c
import p08c_resources as resources


class ResourceTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(); self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)

    def test_no_automatic_tool_install_or_launch_when_measurement_tool_missing(self):
        with mock.patch.dict(sys.modules, {'psutil': None}), mock.patch.object(resources.subprocess, 'Popen') as launch:
            with self.assertRaisesRegex(ValueError, 'not already installed'):
                resources.measure([sys.executable, '-c', 'pass'], self.root, 'missing')
            launch.assert_not_called()

    def test_linked_seed_and_ordinary_owner_library_are_not_eligible(self):
        p08c.write_new(self.root/'probe.json', {'syntheticFlight': False, 'genuineHistoricalSources': True})
        with self.assertRaises(ValueError): resources.retained_inventory(self.root)

    def test_file_accounting_includes_actual_disposable_bytes(self):
        (self.root/'data').write_bytes(b'x'*37)
        folder = self.root/'nested'; folder.mkdir(); (folder/'other').write_bytes(b'y'*19)
        self.assertEqual(56, resources.file_bytes(self.root))

    @unittest.skipUnless(importlib.util.find_spec('psutil'), 'Actual sampler requires already installed psutil; no installation authorized')
    def test_real_owned_process_measurement_and_explicit_timeout_keep_metrics(self):
        metrics = resources.measure([sys.executable, '-c', 'import time;time.sleep(.25)'], self.root, 'success')
        self.assertEqual(0, metrics['exit_code']); self.assertGreater(metrics['sample_count'], 0)
        self.assertGreater(metrics['wall_seconds'], .2)
        self.assertGreater(metrics['sampled_process_tree_peak_rss_bytes'], 0)
        budgets = {**resources.BUDGETS, 'wall_seconds': .15}
        with self.assertRaisesRegex(ValueError, 'stopped/failed'):
            resources.measure([sys.executable, '-c', 'import time;time.sleep(10)'], self.root, 'timeout', budgets)
        stopped = p08c.read(self.root/'timeout-metrics.json')
        self.assertEqual('wall-budget', stopped['stop_reason'])
        self.assertLess(stopped['wall_seconds'], 5)

    @unittest.skipUnless(importlib.util.find_spec('psutil'), 'Actual sampler requires already installed psutil; no installation authorized')
    def test_output_budget_failure_does_not_claim_success(self):
        budgets = {**resources.BUDGETS, 'output_bytes': 16}
        target = self.root/'large-output'
        code = 'from pathlib import Path;import time;Path('+repr(str(target))+').write_bytes(b"x"*100);time.sleep(1)'
        with self.assertRaises(ValueError): resources.measure([sys.executable, '-c', code], self.root, 'disk', budgets)
        self.assertEqual('output-budget', p08c.read(self.root/'disk-metrics.json')['stop_reason'])

    @unittest.skipUnless(importlib.util.find_spec('psutil'), 'Requires already installed sampler')
    def test_unavailable_disk_counters_remain_unknown_not_zero(self):
        import psutil
        with mock.patch.object(psutil.Process, 'io_counters', side_effect=psutil.AccessDenied()):
            result = resources.measure([sys.executable, '-c', 'import time;time.sleep(.2)'], self.root, 'denied-io')
        self.assertIsNone(result['sampled_process_read_bytes']); self.assertIsNone(result['sampled_process_write_bytes'])
        self.assertGreater(result['sampled_process_tree_peak_rss_bytes'], 0)


if __name__ == '__main__': unittest.main()
