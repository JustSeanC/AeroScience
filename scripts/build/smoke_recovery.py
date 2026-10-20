#!/usr/bin/env python3
"""Real-process crash/restart check using temporary data, no simulator or external services."""
import argparse
import http.cookiejar
import json
import os
from pathlib import Path
import subprocess
import tempfile
import time
import urllib.request

REPO = Path(__file__).resolve().parents[2]
DEFAULT_DLL = REPO/'tests/AeroScience.TestHost/bin/Release/net10.0/AeroScience.TestHost.dll'
BASE = 'http://127.0.0.1:5081'


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--host-dll', type=Path, default=DEFAULT_DLL, help='Exact built recovery test-host DLL to launch.')
    dll = parser.parse_args().host_dll.resolve()
    if not dll.is_file():
        parser.error(f'Recovery test-host DLL does not exist: {dll}. Build the test host and pass its output with --host-dll.')
    with tempfile.TemporaryDirectory(prefix='aeroscience-recovery-') as work:
        process = None
        log_path = Path(work)/'console.log'
        env = {**os.environ, 'AeroScience__DataDirectory': str(Path(work)/'data'), 'AeroScience__Port': '5081',
               'AeroScience__SamplingIntervalMs': '250', 'AeroScience__EnableLan': 'false'}

        def start():
            proc = subprocess.Popen([os.environ.get('DOTNET', 'dotnet'), str(dll)], cwd=REPO/'src/AeroScience.Engine', env=env,
                                    stdout=log, stderr=subprocess.STDOUT)
            for _ in range(100):
                if proc.poll() is not None: raise RuntimeError(log_path.read_text(encoding='utf-8'))
                try:
                    with urllib.request.urlopen(BASE+'/api/health', timeout=1) as response:
                        if response.status == 200: return proc
                except OSError: time.sleep(.1)
            proc.kill(); proc.wait(); raise RuntimeError('Engine readiness timed out.')

        def session():
            opener = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))
            def api(path, body=None, csrf=None):
                headers = {'Origin': BASE}
                if body is not None: headers['Content-Type'] = 'application/json'
                if csrf: headers['X-AeroScience-CSRF'] = csrf
                request = urllib.request.Request(BASE+path, data=None if body is None else json.dumps(body).encode(), headers=headers)
                with opener.open(request, timeout=5) as response: return json.load(response)
            token = api('/api/session', {})['csrf']
            return api, token

        with log_path.open('ab', buffering=0) as log:
            try:
                process = start(); api, csrf = session()
                mission = api('/api/missions', {'name': 'Crash recovery', 'mode': 'realistic'}, csrf)
                for _ in range(80):
                    if api('/api/state')['activeMission']['sampleCount'] >= 2: break
                    time.sleep(.1)
                else: raise AssertionError('No samples recorded.')
                # A second engine sharing the data directory must fail before it can run recovery.
                duplicate_env = {**env, 'AeroScience__Port': '5082'}
                duplicate = subprocess.run([os.environ.get('DOTNET', 'dotnet'), str(dll)], cwd=REPO/'src/AeroScience.Engine', env=duplicate_env,
                                           stdout=log, stderr=subprocess.STDOUT, timeout=10)
                assert duplicate.returncode != 0
                assert api('/api/state')['activeMission']['id'] == mission['id']
                process.kill(); process.wait(); process = start(); api, csrf = session()
                assert api('/api/state')['activeMission'] is None
                recovered = api('/api/missions/'+mission['id'])['mission']
                assert recovered['status'] == 'interrupted' and recovered['mode'] == 'realistic' and recovered['sampleCount'] >= 2
                completed = api('/api/missions', {'name': 'Restarted Universal'}, csrf)
                assert completed['mode'] == 'universal'
                api('/api/missions/'+completed['id']+'/end', {}, csrf)
                process.terminate(); process.wait(timeout=10); process = start(); api, csrf = session()
                assert api('/api/missions/'+completed['id'])['mission']['status'] == 'completed'
                print(f"PASS: actual-process crash/restart retained {recovered['sampleCount']} samples and Realistic mode; Universal default/completion persisted; duplicate engine refused.")
            finally:
                if process and process.poll() is None: process.terminate(); process.wait(timeout=10)


if __name__ == '__main__': main()
