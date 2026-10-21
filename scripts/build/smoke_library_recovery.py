#!/usr/bin/env python3
"""Real-process Library approval/budget kill recovery; synthetic persistence fixtures only."""
import argparse
import json
import os
from pathlib import Path
import subprocess
import tempfile
import time


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--host-dll', type=Path, required=True)
    dll = parser.parse_args().host_dll.resolve()
    if not dll.is_file():
        parser.error(f'Test-host DLL not found: {dll}')
    command = [os.environ.get('DOTNET', 'dotnet'), str(dll), '--library-probe']
    with tempfile.TemporaryDirectory(prefix='aeroscience-library-crash-') as temporary:
        for phase in ('reserved', 'charged'):
            folder = Path(temporary)/phase
            folder.mkdir()
            signal = folder/'kill-ready'
            with (folder/'process.log').open('wb') as log:
                process = subprocess.Popen(command+[phase, str(folder), str(signal)], stdout=log, stderr=subprocess.STDOUT)
                try:
                    deadline = time.monotonic()+20
                    while not signal.exists():
                        if process.poll() is not None:
                            raise AssertionError((folder/'process.log').read_text(encoding='utf-8'))
                        if time.monotonic() > deadline:
                            raise TimeoutError('Library durable budget kill point not reached')
                        time.sleep(.02)
                    process.kill()
                    process.wait(timeout=10)
                finally:
                    if process.poll() is None:
                        process.kill()
                        process.wait(timeout=10)
            inspected = subprocess.run(command+['inspect', str(folder)], capture_output=True, text=True, timeout=20, check=True)
            job = json.loads(inspected.stdout)
            assert job['state'] == 'Interrupted', job
            assert job['approvalId'] and job['preview']['selectionHash'] == 'b'*64
            assert len(job['items']) == 1 and job['items'][0]['sourceId'] is None
            assert job['downloadedBytes'] == (1234 if phase == 'charged' else 0)
            assert job['downloadedBytes']+job['uncertainBytes'] == 65536
            assert job['items'][0]['reservedBytes'] == 0
            second = subprocess.run(command+['inspect', str(folder)], capture_output=True, text=True, timeout=20, check=True)
            assert json.loads(second.stdout)['uncertainBytes'] == job['uncertainBytes']
    print('PASS: 2 actual process kills retained approved Library intent, charged bytes and unresolved read allowances; recovery is idempotent and publishes no fictional source/mission.')


if __name__ == '__main__':
    main()
