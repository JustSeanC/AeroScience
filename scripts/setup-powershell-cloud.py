#!/usr/bin/env python3
"""Install the official pinned Linux x64 PowerShell archive under the writable cloud tools root."""
from pathlib import Path
import hashlib
import platform
import tarfile
import tempfile
import urllib.request
if (platform.system(),platform.machine())!=('Linux','x86_64'):
    raise SystemExit('Install PowerShell 7 for this platform using its official instructions.')
target=Path('/workspace/.tools/pwsh')
if (target/'pwsh').is_file():raise SystemExit(0)
url='https://github.com/PowerShell/PowerShell/releases/download/v7.5.4/powershell-7.5.4-linux-x64.tar.gz'
expected='1fd7983fe56ca9e6233f126925edb24bf6b6b33e356b69996d925c4db94e2fef'
with tempfile.TemporaryDirectory(prefix='aeroscience-pwsh-') as work:
    archive=Path(work)/'powershell.tar.gz';urllib.request.urlretrieve(url,archive)
    with archive.open('rb') as stream:
        if hashlib.file_digest(stream,'sha256').hexdigest()!=expected:raise SystemExit('Official PowerShell archive SHA-256 mismatch.')
    target.mkdir(parents=True,exist_ok=True)
    with tarfile.open(archive) as tar:tar.extractall(target,filter='data')
    (target/'pwsh').chmod(0o755)
print('Official PowerShell 7.5.4 archive SHA-256 verified; cloud helper fixtures can run.')
