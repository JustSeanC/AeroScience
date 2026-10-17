#!/usr/bin/env python3
"""Check committed offline assets. No network, downloads, or tile caching."""
import hashlib
import json
from pathlib import Path
ROOT = Path(__file__).resolve().parents[2]
WEB = ROOT/'src/AeroScience.Engine/wwwroot'
def verify(web=WEB):
    manifest = json.loads((web/'assets-manifest.json').read_text())
    for name, digest in manifest['files'].items():
        path=web/name
        if not path.is_file() or hashlib.sha256(path.read_bytes()).hexdigest()!=digest:
            raise ValueError('Bundled web asset missing or changed: '+name)
    return len(manifest['files'])
if __name__=='__main__': print(f'Verified {verify()} bundled geography/library/license files. No external map service required.')
