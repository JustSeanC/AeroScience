#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
source scripts/env.sh
if ! command -v dotnet >/dev/null || [[ "$(dotnet --version)" != 10.0.402 ]]; then
  python3 - <<'PY'
import hashlib, pathlib, platform, tarfile, tempfile, urllib.request
if (platform.system(), platform.machine()) != ('Linux', 'x86_64'):
    raise SystemExit('Install the pinned .NET SDK for this platform; this bootstrap supports Linux x64 only.')
target = pathlib.Path('/workspace/.tools/dotnet')
target.mkdir(parents=True, exist_ok=True)
url = 'https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0.402/dotnet-sdk-10.0.402-linux-x64.tar.gz'
expected = '1904a98dda2db34ba78ba3c4fb1b224455329af91c341d83547843c1839e112b04ae699ee92fa2449a8f62249196a938723078c0c60b9f65c88539d5687729c2'
with tempfile.TemporaryDirectory(prefix='aeroscience-sdk-') as work:
    archive = pathlib.Path(work)/'sdk.tar.gz'
    urllib.request.urlretrieve(url, archive)
    with archive.open('rb') as f:
        if hashlib.file_digest(f, 'sha512').hexdigest() != expected:
            raise SystemExit('Official .NET archive SHA-512 mismatch; refusing installation.')
    with tarfile.open(archive) as f:
        f.extractall(target, filter='data')
PY
fi
dotnet restore AeroScience.sln --locked-mode
dotnet build AeroScience.sln -c Release --no-restore --nologo
npm ci --ignore-scripts
npm run check:web
