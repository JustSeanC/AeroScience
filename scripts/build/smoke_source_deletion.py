#!/usr/bin/env python3
"""Actual-process source-deletion kill recovery; synthetic originals only."""
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
    command = [os.environ.get('DOTNET', 'dotnet'), str(dll), '--source-deletion-probe']
    with tempfile.TemporaryDirectory(prefix='aeroscience-source-delete-kill-') as temporary:
        for phase in ('quarantined', 'before-commit', 'committed'):
            folder = Path(temporary)/phase
            folder.mkdir()
            marker = folder/'kill-ready'
            with (folder/'process.log').open('wb') as log:
                process = subprocess.Popen(command+[phase, str(folder), str(marker)], stdout=log, stderr=subprocess.STDOUT)
                try:
                    deadline = time.monotonic()+20
                    while not marker.exists():
                        if process.poll() is not None:
                            raise AssertionError((folder/'process.log').read_text(encoding='utf-8'))
                        if time.monotonic() > deadline:
                            raise TimeoutError('Source deletion durable kill point not reached')
                        time.sleep(.02)
                    process.kill()
                    process.wait(timeout=10)
                finally:
                    if process.poll() is None:
                        process.kill()
                        process.wait(timeout=10)
            for _ in range(2):
                check = subprocess.run(command+['inspect', str(folder)], capture_output=True, text=True, timeout=20, check=True)
                result = json.loads(check.stdout)
                assert result == {'sources': 0 if phase == 'committed' else 2, 'products': 0, 'datasets': 0, 'backupPreserved': True, 'userOriginalsPreserved': True}, result
    print('PASS: 3 actual process kills recovered exact source ownership/SQLite receipts idempotently; original hashes, user files and prior backups preserved; no mission/science observations created.')


if __name__ == '__main__':
    main()
