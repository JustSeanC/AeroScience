#!/usr/bin/env python3
"""Kill real test-host processes at evidence transaction boundaries; isolated synthetic data only."""
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
    command = [os.environ.get('DOTNET', 'dotnet'), str(dll), '--evidence-probe']
    cases = 0
    with tempfile.TemporaryDirectory(prefix='aeroscience-evidence-crash-') as work:
        for phase in ('create', 'close'):
            for point in ('after-session', 'after-event', 'after-checkpoint', 'before-commit', 'after-commit'):
                folder = Path(work)/f'{phase}-{point}'
                folder.mkdir()
                database, signal = folder/'missions.db', folder/'kill-ready.json'
                with (folder/'process.log').open('wb') as log:
                    process = subprocess.Popen(command+[phase, str(database), str(signal), point], stdout=log, stderr=subprocess.STDOUT)
                    try:
                        deadline = time.monotonic()+20
                        while not signal.exists():
                            if process.poll() is not None:
                                raise AssertionError((folder/'process.log').read_text(encoding='utf-8'))
                            if time.monotonic() > deadline:
                                raise TimeoutError(f'Kill point was not reached: {phase}/{point}')
                            time.sleep(.02)
                        process.kill()
                        process.wait(timeout=10)
                    finally:
                        if process.poll() is None:
                            process.kill()
                            process.wait(timeout=10)
                inspected = subprocess.run(command+['inspect', str(database)], capture_output=True, text=True, timeout=20, check=True)
                result = json.loads(inspected.stdout)
                committed = point == 'after-commit'
                expected_sessions = 1 if phase == 'close' or committed else 0
                assert result['beforeSessionCount'] == expected_sessions, (phase, point, result)
                assert result['samples'] == expected_sessions
                assert result['before']['revision'] == ((2 if committed else 1) if phase == 'close' else int(committed))
                assert result['events'] == result['before']['revision']+1  # interruption appended exactly once
                assert result['after']['phase'] == 'stopped' and result['after']['currentSessionId'] is None
                assert result['mission']['status'] == 'interrupted'
                if expected_sessions:
                    session = result['sessions'][0]
                    if phase == 'close' and committed:
                        assert session['status'] == 'ended' and session['endReason'] == 'manual'
                        assert session['endedUtc'] == '2026-10-20T12:00:02+00:00'
                    else:
                        assert session['status'] == 'interrupted' and session['endedUtc'] is None
                    assert result['intents'][0]['state'] == 'Cancelled'
                cases += 1
    print(f'PASS: {cases} real-process kills retained atomic session/event/checkpoint/input/intent evidence; restart interrupted the flight without auto-resume. Synthetic persistence fixtures, not MSFS/scientific acceptance.')


if __name__ == '__main__':
    main()
