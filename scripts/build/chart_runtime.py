"""Assemble/verify the pinned Windows chart runtime, including data and static-code notices.

This records distribution identity; it is not a chart-neatline or release-acceptance assertion.
No original chart, SDK, secret, development tool or public installer is packaged.
"""
import argparse
import hashlib
import importlib.util
import json
from pathlib import Path
import re
import shutil
import stat

ROOT = Path(__file__).resolve().parents[2]
DLLS = {'gdal.dll', 'geotiff.dll', 'json-c.dll', 'libcurl.dll', 'liblzma.dll',
        'proj_9.dll', 'sqlite3.dll', 'tiff.dll', 'z.dll'}
COMPONENTS = {'gdal', 'libgeotiff', 'json-c', 'curl', 'liblzma', 'proj',
              'sqlite3', 'tiff', 'zlib', 'libjpeg-turbo', 'nlohmann-json'}
MAXIMUM_BYTES = 192 * 1024 * 1024
MAXIMUM_FILE_BYTES = 32 * 1024 * 1024
spec = importlib.util.spec_from_file_location('chart_native_audit', ROOT/'tests/AeroScience.ReducedGisProbe/inspect_native.py')
audit = importlib.util.module_from_spec(spec)
spec.loader.exec_module(audit)


def unlinked(path):
    for p in [path, *path.parents]:
        if not p.exists() and not p.is_symlink():
            continue
        info = p.lstat()
        if stat.S_ISLNK(info.st_mode) or getattr(info, 'st_file_attributes', 0) & 0x400:
            raise ValueError('Linked/reparse runtime paths are unsupported.')


def regular(path):
    unlinked(path)
    info = path.stat()
    if not stat.S_ISREG(info.st_mode) or not 0 < info.st_size <= MAXIMUM_FILE_BYTES:
        raise ValueError('Invalid runtime file type or bounded size.')
    return info.st_size


def identity(path):
    size = regular(path)
    with path.open('rb') as stream:
        digest = hashlib.file_digest(stream, 'sha256').hexdigest()
    return {'bytes': size, 'sha256': digest}


def verify(folder):
    regular(folder/'manifest.json')
    manifest = json.loads((folder/'manifest.json').read_text(encoding='utf-8'))
    if manifest.get('version') != 1 or manifest.get('runtime') != 'win-x64' or manifest.get('gdalVersion') != '3.12.4':
        raise ValueError('Unsupported chart-runtime identity/version.')
    if not re.fullmatch('[0-9a-f]{40}', manifest.get('sourceSha', '')) or manifest.get('networkGridsAllowed') is not False or manifest.get('publicDistributionApproved') is not False:
        raise ValueError('Unsupported source/network/distribution policy.')
    records = manifest.get('files')
    if not isinstance(records, list) or not 12 <= len(records) <= 256:
        raise ValueError('Invalid runtime inventory count.')
    names = set()
    total = 0
    for record in records:
        name = record['path']
        if not isinstance(name, str) or not re.fullmatch(r'[A-Za-z0-9_. /-]+', name) or \
                name.startswith('/') or '\\' in name or any(p in ('', '.', '..') for p in name.split('/')) or name.lower() in names:
            raise ValueError('Unsafe or duplicate runtime member.')
        names.add(name.lower())
        actual = identity(folder/name)
        if actual != {k: record[k] for k in ('bytes', 'sha256')}:
            raise ValueError('Runtime content differs from its exact manifest.')
        total += actual['bytes']
    actual_names = set()
    for path in folder.rglob('*'):
        unlinked(path)
        if not path.is_dir():
            actual_names.add(path.relative_to(folder).as_posix().lower())
    if names | {'manifest.json'} != actual_names or total > MAXIMUM_BYTES or manifest.get('totalBytes') != total:
        raise ValueError('Unlisted runtime content or incorrect bounded total.')
    if {p.name.lower() for p in (folder/'bin').iterdir()} != DLLS or 'share/proj/proj.db' not in names:
        raise ValueError('Runtime DLL/PROJ inventory differs from the reviewed graph.')
    for component in COMPONENTS:
        if 'licenses/'+component+'.txt' not in names:
            raise ValueError('Missing direct, static or header-only component notice.')
    native = audit.verify(folder/'bin')
    if not native['importsAccountedFor']:
        raise ValueError('Native import audit failed.')
    return manifest


def assemble(install, output, port_receipt, source_sha):
    if not re.fullmatch('[0-9a-f]{40}', source_sha):
        raise ValueError('Require the exact source commit.')
    if output.exists():
        raise ValueError('Preserve existing runtime artifacts; choose a new output directory.')
    unlinked(output)
    recipe = json.loads(port_receipt.read_text(encoding='utf-8-sig'))
    if recipe.get('vcpkgCommit') != '1391150599c0b9dc6f2a3c79c50eff6c94b077d1' or \
            recipe.get('gdalVersion') != '3.12.4' or recipe.get('jpegLibraryLinkage') != 'static' or recipe.get('turbojpegEnabled') is not False:
        raise ValueError('Unreviewed native source/CRT configuration.')
    native = audit.verify(install/'bin')
    if {p.name.lower() for p in (install/'bin').glob('*.dll')} != DLLS:
        raise ValueError('Native component graph changed; review it before packaging.')
    sources = [(p, 'bin/'+p.name) for p in sorted((install/'bin').glob('*.dll'))]
    sources.append((install/'share/proj/proj.db', 'share/proj/proj.db'))
    for p in sorted((install/'share/gdal').rglob('*')):
        if p.is_file():
            relative = p.relative_to(install/'share/gdal').as_posix()
            sources.append((p, 'share/gdal/'+relative))
    for component in sorted(COMPONENTS):
        sources.append((install/'share'/component/'copyright', 'licenses/'+component+'.txt'))
    if len(sources) > 256:
        raise ValueError('Resource inventory exceeds its reviewed bound.')
    # Validate every input before publishing any output, and verify copied bytes again.
    records = [dict(path=name, **identity(path)) for path, name in sources]
    if sum(r['bytes'] for r in records) > MAXIMUM_BYTES:
        raise ValueError('Runtime exceeds the bounded distribution size.')
    output.mkdir(parents=True)
    try:
        for source, relative in sources:
            destination = output/relative
            destination.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(source, destination)
        manifest = {'version': 1, 'runtime': 'win-x64', 'gdalVersion': '3.12.4',
                    'sourceSha': source_sha, 'recipe': recipe, 'files': records,
                    'totalBytes': sum(r['bytes'] for r in records),
                    'dllBytes': sum(r['bytes'] for r in records if r['path'].startswith('bin/')),
                    'networkGridsAllowed': False, 'nativeAudit': native,
                    'publicDistributionApproved': False,
                    'limitations': ['Exact runtime/data/notices identity, not chart placement/neatline acceptance.',
                                   'OSV-2026-1068: TurboJPEG API omitted; component advisory retained for review.',
                                   'Only the isolated private validation path is eligible; public release gates remain unchanged.']}
        (output/'manifest.json').write_text(json.dumps(manifest, indent=2)+'\n', encoding='utf-8')
        return verify(output)
    except BaseException:
        # Own newly-created scratch only; never replace an earlier artifact or original.
        shutil.rmtree(output)
        raise


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest='command', required=True)
    pack = sub.add_parser('assemble')
    pack.add_argument('--install', required=True, type=Path)
    pack.add_argument('--output', required=True, type=Path)
    pack.add_argument('--port-receipt', required=True, type=Path)
    pack.add_argument('--source-sha', required=True)
    check = sub.add_parser('verify')
    check.add_argument('--folder', required=True, type=Path)
    args = parser.parse_args()
    result = assemble(args.install, args.output, args.port_receipt, args.source_sha) if args.command == 'assemble' else verify(args.folder)
    print(json.dumps({'verified': True, 'files': len(result['files']), 'totalBytes': result['totalBytes'],
                      'dllBytes': result['dllBytes'], 'sourceSha': result['sourceSha']}))
