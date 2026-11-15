#!/usr/bin/env python3
"""Offline P08c evidence runner. Uses retained inputs and existing scientific probes.

No dataset/tool installation, downloads, app commands, or owner Library access.
Each invocation requires a new output directory; failures retain their diagnostics.
"""
import argparse
import datetime as dt
import hashlib
import json
import math
import os
from pathlib import Path
import platform
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[2]
SCHEMA = 'aeroscience-p08c-evidence-1'
# These existing small probes declare provider-specific metadata and geometry.
# Other genuine originals need their own reviewed probe, not a guessed identity.
SMALL_ORIGINALS = {'terrain': '4c043d10024c8af4077c45d9ed6778141ccaf0837298ea8a521c9f832ce9a7f0',
                   'lidar': '68c4b0ed247501a15d020d9a758d0345904f38785235732bd8e3386a3fca71c1'}
TOLERANCES = {
    'terrain': {'nativeValueAbsolute': 0, 'pixelCenterDegrees': 1e-12,
                'nativeCellFractionAbsolute': 1e-7, 'maskSymmetricDifferencePixelArea': 1e-7},
    'lidar': {'rawCoordinatesAndAttributes': 0, 'geographicDegrees': 1e-10,
              'scanAngleDegrees': 1e-12, 'recordMembershipAndSupport': 0},
}


def require(condition, message):
    if not condition:
        raise ValueError(message)


def safe_path(path):
    path = Path(os.path.abspath(path))
    for part in [*reversed(path.parents), path]:
        require(not part.is_symlink() and not (hasattr(part, 'is_junction') and part.is_junction()),
                'Links/reparse directories are not evidence locations.')
    return path


def digest(path):
    path = safe_path(path)
    require(path.is_file(), 'Evidence file is missing: ' + path.name)
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def read(path):
    path = safe_path(path)
    require(path.stat().st_size <= 16 * 1024 * 1024, 'JSON evidence exceeds the 16 MiB limit.')
    def pairs(values):
        result = {}
        for key, value in values:
            require(key not in result, 'Duplicate JSON field: ' + key)
            result[key] = value
        return result
    def nonfinite(value):
        raise ValueError('Nonfinite JSON evidence: ' + value)
    return json.loads(path.read_text(encoding='utf-8'), object_pairs_hook=pairs, parse_constant=nonfinite)


def write_new(path, data):
    path = safe_path(path)
    with path.open('x', encoding='utf-8', newline='\n') as stream:
        json.dump(data, stream, indent=2, ensure_ascii=False, allow_nan=False)
        stream.write('\n')


def fresh(path):
    path = safe_path(path)
    require(not path.exists(), 'Preserve earlier evidence. Choose a fresh output directory.')
    path.mkdir(parents=True)
    return path


def identity(host):
    host = safe_path(host)
    require(host.name == 'AeroScience.TestHost.dll', 'Use the existing development TestHost DLL.')
    source = subprocess.check_output(['git', '-C', str(ROOT), 'rev-parse', 'HEAD'], text=True).strip()
    branch = subprocess.check_output(['git', '-C', str(ROOT), 'branch', '--show-current'], text=True).strip()
    require(branch == 'develop/v0.8.0', 'P08c runs only on develop/v0.8.0.')
    # The source identity is a build claim, paired with actual loaded file hashes.
    # Refuse dirty runtime/probe inputs; tools may have a separately recorded identity.
    changed = subprocess.check_output(['git', '-C', str(ROOT), 'status', '--porcelain', '--',
        'src', 'native', 'tests/AeroScience.TestHost', 'tests/AeroScience.Tests'], text=True)
    require(not changed, 'Commit runtime/probe changes and build before collecting evidence.')
    files = {p.name: digest(p) for p in sorted(host.parent.glob('*.dll'))}
    native = host.parent / 'LidarRuntime'
    if native.exists():
        files.update({'LidarRuntime/' + p.name: digest(p) for p in sorted(native.iterdir()) if p.is_file()})
    require(host.name in files, 'Build the existing TestHost first.')
    return {'source_commit': source, 'branch': branch, 'build_files_sha256': files,
            'scope': 'Development TestHost build; not the sealed owner Windows package',
            'harness_sha256': digest(__file__)}


def inventory(manifest):
    require(isinstance(manifest, dict) and manifest.get('schema') == 'aeroscience-p08c-inputs-1', 'Unknown input inventory schema.')
    items = manifest.get('inputs')
    require(isinstance(items, list) and 1 <= len(items) <= 64, 'Select 1–64 retained originals.')
    results = []
    for item in items:
        require(isinstance(item, dict), 'Invalid inventory item.')
        for key in ['id', 'path', 'kind', 'provider_project', 'source_date_utc', 'horizontal_crs',
                    'vertical_datum', 'units', 'native_shape_or_count', 'attributes', 'license_access', 'retained_authorization']:
            require(key in item, 'Inventory needs ' + key + ' (use null for genuinely unknown metadata).')
        require(item['kind'] in ['dem', 'dsm', 'las', 'laz', 'ept-node', 'satellite'], 'Unsupported source kind.')
        require(item['retained_authorization'] and item['license_access'], 'Retained access/license context is required.')
        path = safe_path(item['path'])
        require(0 < path.stat().st_size <= 2 * 1024**3, 'Each selected original must be nonempty and at most 2 GiB.')
        value = {**item, 'path': str(path), 'bytes': path.stat().st_size, 'sha256': digest(path)}
        if item.get('expected_sha256'):
            require(value['sha256'] == item['expected_sha256'], 'Retained original differs from its recorded SHA-256.')
        results.append(value)
    require(len({i['id'] for i in results}) == len(results), 'Input IDs must be distinct.')
    return results


def execute(command, output, label, timeout=600):
    # Argument arrays only; never execute shell text supplied by an evidence file.
    with (output / (label + '.log')).open('xb') as log:
        result = subprocess.run([str(v) for v in command], cwd=ROOT, stdout=log,
                                stderr=subprocess.STDOUT, timeout=timeout, check=False)
    require(result.returncode == 0, label + ' failed. Keep this attempt and its log.')


def artifact_records(output):
    files = sorted(p for p in output.rglob('*') if p.is_file())
    require(len(files) <= 10000, 'Too many evidence artifacts.')
    return [{'file': str(p.relative_to(output)).replace(os.sep, '/'), 'bytes': p.stat().st_size,
             'sha256': digest(p)} for p in files if p.name != 'receipt.json']


def numerical(args):
    original = inventory(read(args.inventory))
    selected = next((v for v in original if v['id'] == args.input_id), None)
    require(selected is not None, 'Choose a declared inventory input.')
    require(selected['kind'] in (['dem'] if args.kind == 'terrain' else ['las', 'laz', 'ept-node']),
            'This bounded probe does not certify this source family.')
    require(selected['sha256'] == SMALL_ORIGINALS[args.kind],
            'This existing small probe is limited to its retained provider original. Other datasets remain untested.')
    build = identity(args.host)
    output = fresh(args.output)
    # Fixed tolerances are retained BEFORE either production or reference execution.
    plan = {'schema': SCHEMA, 'evidence_kind': 'independent-numerical', 'operation': args.kind,
        'runtime': build, 'inputs': original, 'selected_input': selected['id'],
        'tolerances': TOLERANCES[args.kind], 'synthetic_flight': True,
        'historical_sources': True, 'installed_acceptance': 'Not Tested',
        'network_transfer_bytes': 0, 'network_scope': 'Offline probe mode; not an OS network capture',
        'started_utc': dt.datetime.now(dt.timezone.utc).isoformat()}
    write_new(output/'plan.json', plan)
    receipt = {**plan, 'outcome': 'Failed'}
    try:
        flag = '--terrain-elevation-probe' if args.kind == 'terrain' else '--lidar-probe'
        command = [args.dotnet, args.host, flag]
        if args.kind == 'lidar':
            command.append('--offline-local')
        execute([*command, selected['path'], output/'probe'], output, 'application')
        oracle = ROOT/'scripts/validation'/('verify-terrain-elevation.py' if args.kind == 'terrain' else 'verify-lidar-points.py')
        execute([args.reference_python, '-I', oracle, output/'probe'], output, 'independent-reference')
        reference = read(output/'probe/independent-comparison.json')
        require(reference.get('state') == 'Passed', 'Independent reference did not pass.')
        require(all(digest(v['path']) == v['sha256'] for v in original), 'An original changed during verification.')
        receipt.update(outcome='Passed', independent_reference=reference, oracle_sha256=digest(oracle))
    except Exception as exc:
        receipt['error'] = str(exc)
        raise
    finally:
        receipt['finished_utc'] = dt.datetime.now(dt.timezone.utc).isoformat()
        receipt['artifacts'] = artifact_records(output)
        write_new(output/'receipt.json', receipt)
    return output/'receipt.json'


def verify_receipt(path, runtime_source=None):
    """Check local evidence bytes and identity, not reviewer qualification or release approval."""
    path = safe_path(path)
    value = read(path)
    require(value.get('schema') == SCHEMA and value.get('outcome') == 'Passed', 'A successful versioned P08c measured receipt is required.')
    require(value.get('evidence_kind') == 'independent-numerical', 'This is not independent numerical evidence.')
    runtime = value.get('runtime', {})
    require(runtime.get('source_commit') == runtime_source if runtime_source else bool(runtime.get('source_commit')),
            'Evidence belongs to another source/build. Do not transfer acceptance.')
    require(bool(runtime.get('build_files_sha256')), 'Exact tested build hashes are required.')
    require(value.get('independent_reference', {}).get('state') == 'Passed', 'An independent reference is required.')
    require(value.get('tolerances') == TOLERANCES.get(value.get('operation')), 'Tolerance plan changed or is missing.')
    require(value.get('historical_sources') is True and bool(value.get('inputs')), 'Identify the genuine retained inputs.')
    artifacts = value.get('artifacts')
    require(isinstance(artifacts, list) and 1 <= len(artifacts) <= 10000, 'Measured artifacts are required.')
    seen = set()
    require(sum(a['bytes'] for a in artifacts) <= 4*1024**3, 'Evidence bundle exceeds the 4 GiB verification bound.')
    for artifact in artifacts:
        relative = Path(artifact['file'])
        require(not relative.is_absolute() and ':' not in str(relative) and '\\' not in str(relative)
                and '..' not in relative.parts and str(relative) not in seen, 'Unsafe/duplicate artifact reference.')
        seen.add(str(relative))
        target = safe_path(path.parent/relative)
        require(target.stat().st_size == artifact['bytes'] and digest(target) == artifact['sha256'], 'Measured evidence is missing or changed.')
    require('plan.json' in seen and 'probe/independent-comparison.json' in seen, 'Retain the original plan and reference artifact.')
    plan = read(path.parent/'plan.json')
    require(all(value.get(k) == plan.get(k) for k in plan), 'Receipt differs from its pre-execution plan.')
    require(value['independent_reference'] == read(path.parent/'probe/independent-comparison.json'), 'Reference summary changed.')
    return value


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest='action', required=True)
    inv = sub.add_parser('inventory'); inv.add_argument('--inventory', type=Path, required=True); inv.add_argument('--output', type=Path, required=True)
    run = sub.add_parser('numerical')
    run.add_argument('--kind', choices=['terrain', 'lidar'], required=True)
    run.add_argument('--inventory', type=Path, required=True); run.add_argument('--input-id', required=True)
    run.add_argument('--host', type=Path, required=True); run.add_argument('--dotnet', default=os.environ.get('DOTNET', 'dotnet'))
    run.add_argument('--reference-python', type=Path, required=True); run.add_argument('--output', type=Path, required=True)
    verify = sub.add_parser('verify'); verify.add_argument('receipt', type=Path)
    args = parser.parse_args()
    try:
        if args.action == 'inventory':
            value = inventory(read(args.inventory)); write_new(args.output, {'schema': 'aeroscience-p08c-inventory-1', 'inputs': value})
            print(args.output)
        elif args.action == 'numerical':
            print(numerical(args))
        else:
            verify_receipt(args.receipt); print('Measured evidence bytes verified; no installed/release approval.')
    except (ValueError, OSError, subprocess.SubprocessError, KeyError) as exc:
        print('P08c refused: ' + str(exc), file=sys.stderr); return 1
    return 0


if __name__ == '__main__':
    sys.exit(main())
