"""Kill a real FAA worker during a durably reserved synthetic network read.

No live provider access or scientific observations. Uses the production aviation
SQLite, approval, worker, staging and offline verification path in separate hosts.
"""
import argparse
import json
import pathlib
import subprocess
import tempfile
import time


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--host-dll', required=True, type=pathlib.Path)
    args = parser.parse_args()
    with tempfile.TemporaryDirectory(prefix='aeroscience-aviation-recovery-') as temp:
        root = pathlib.Path(temp) / 'aviation'
        command = ['dotnet', str(args.host_dll.resolve()), '--aviation-probe']
        process = subprocess.Popen(command + ['interrupt', str(root)], stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        try:
            deadline = time.monotonic() + 30
            while not (root / 'read-reserved.marker').exists():
                if process.poll() is not None:
                    raise RuntimeError('Aviation interruption probe stopped early: ' + process.communicate()[1].decode())
                if time.monotonic() > deadline:
                    raise TimeoutError('Aviation read was not durably reserved within 30 seconds')
                time.sleep(.05)
            process.kill()
            process.communicate(timeout=10)
        finally:
            if process.poll() is None:
                process.kill()
                process.communicate(timeout=10)
        for mode in ('recover', 'resume', 'offline'):
            result = subprocess.run(command + [mode, str(root)], capture_output=True, text=True, timeout=30)
            if result.returncode:
                raise RuntimeError(f'Aviation {mode} failed: {result.stdout}\n{result.stderr}')
        recovered = json.loads((root / 'aviation-recover-receipt.json').read_text())
        resumed = json.loads((root / 'aviation-resume-receipt.json').read_text())
        offline = json.loads((root / 'aviation-offline-receipt.json').read_text())
        assert recovered['jobs'][0]['state'] == 'Interrupted'
        assert recovered['jobs'][0]['chargedBytes'] > 0
        assert resumed['jobs'][0]['state'] == 'Completed'
        assert resumed['jobs'][0]['chargedBytes'] > resumed['jobs'][0]['downloadedBytes']
        assert offline['transfers'] == 0 and not offline['missionCreated']
        assert resumed['jobs'][0]['originalSha256'] == offline['jobs'][0]['originalSha256']
        print('PASS: aviation process-kill, durable approval/allowance, explicit recovery, atomic original retention and offline hash verification. Synthetic fixture; no FAA geographic acceptance.')


if __name__ == '__main__':
    main()
