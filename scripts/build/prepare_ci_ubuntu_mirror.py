"""Disposable CI transport fallback; Ubuntu's original signing trust remains required."""
import hashlib
import os
from pathlib import Path
import re
import subprocess
import tempfile
import urllib.request
from urllib.parse import urlsplit

MIRROR = 'https://mirror.math.princeton.edu/pub/ubuntu/'
ENTRY = MIRROR + '\tpriority:0'
SUITES = ('noble', 'noble-updates', 'noble-backports', 'noble-security')
KEYRING = '/usr/share/keyrings/ubuntu-archive-keyring.gpg'
CANONICAL = {'azure.archive.ubuntu.com', 'archive.ubuntu.com', 'security.ubuntu.com'}


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, request, response, code, message, headers, url):
        return None


def verify_release(suite, download=None, verify=None):
    if suite not in SUITES:
        raise ValueError('Only the fixed Ubuntu 24.04 suites are supported.')
    url = MIRROR + 'dists/' + suite + '/InRelease'
    if download is None:
        with urllib.request.build_opener(NoRedirect()).open(url, timeout=15) as response:
            body = response.read(1048577)
    else:
        body = download(url)
    if len(body) > 1048576 or not body.startswith(b'-----BEGIN PGP SIGNED MESSAGE-----'):
        raise ValueError('Expected bounded signed Ubuntu release metadata.')
    if verify is None:
        with tempfile.TemporaryDirectory(prefix='aeroscience-ci-ubuntu-') as folder:
            path = Path(folder) / 'InRelease'
            path.write_bytes(body)
            subprocess.run(['gpgv', '--keyring', KEYRING, str(path)], check=True,
                           capture_output=True, timeout=15)
    else:
        verify(body)
    for field, value in [('Origin', 'Ubuntu'), ('Suite', suite), ('Codename', 'noble')]:
        if not re.search(rb'(?m)^' + field.encode() + b': ' + value.encode() + rb'\r?$', body):
            raise ValueError('Signed Ubuntu release identity mismatch: ' + field)
    return hashlib.sha256(body).hexdigest()


def prepare(path, verify=verify_release, log=print):
    path = Path(path)
    original = path.read_bytes()
    lines = original.decode('utf-8').splitlines()
    if any(line.split() == ENTRY.split() for line in lines):
        return False
    urls = [urlsplit(line.split()[0]) for line in lines if line.strip() and not line.lstrip().startswith('#')]
    if not any(url.scheme in ('http', 'https') and url.hostname in CANONICAL and url.path.rstrip('/') == '/ubuntu'
               and not url.username and not url.password for url in urls):
        raise ValueError('Expected the hosted runner official Ubuntu mirror list; custom sources are preserved.')
    # Verify every suite using the Ubuntu keyring already trusted by the stock
    # runner. No imported key, signature bypass, new package or suite is allowed.
    for suite in SUITES:
        digest = verify(suite)
        log('Verified Ubuntu ' + suite + ' InRelease with existing archive trust: ' + digest)
    with path.open('ab') as output:
        output.write(('\n# AeroScience disposable CI signed Ubuntu transport fallback\n' + ENTRY + '\n').encode())
    return True


def main():
    if os.environ.get('GITHUB_ACTIONS') != 'true' or os.environ.get('RUNNER_OS') != 'Linux':
        raise SystemExit('This helper is restricted to disposable Linux GitHub Actions runners.')
    if os.geteuid() != 0:
        raise SystemExit('The CI workflow must explicitly run this helper with sudo.')
    prepare('/etc/apt/apt-mirrors.txt')


if __name__ == '__main__':
    main()
