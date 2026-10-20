#!/usr/bin/env python3
"""Bind the Inno Setup EXE to verified SDK payloads and actual installer acceptance."""
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import struct
import tempfile
import package_release as p


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def write_json(path, value):
    # The receipt fingerprint must be identical on Windows and Linux.
    path.write_text(json.dumps(value, sort_keys=True, indent=2)+'\n', encoding='utf-8', newline='\n')


def recipe_sha():
    # Git checkouts may use CRLF or LF; both compile the same recipe.
    source = (p.REPO/'scripts/installer/AeroScience.iss').read_text(encoding='utf-8-sig')
    return hashlib.sha256(source.encode('utf-8')).hexdigest()


def pe(path):
    data = path.read_bytes()
    p.require(len(data) > 1024 and data[:2] == b'MZ', 'Installer/prerequisite must be a real Windows PE executable.')
    offset = struct.unpack_from('<I', data, 60)[0]
    p.require(offset+6 < len(data) and data[offset:offset+4] == b'PE\0\0', 'Invalid installer/prerequisite PE header.')


def stage(candidate, destination, expected_commit):
    commit, digest = p.validate_payload(candidate)
    p.require(commit == expected_commit, 'Installer recipe/source must match the SDK/application candidate commit.')
    p.require(not destination.exists(), 'Installer payload already exists; preserve it and use another output directory.')
    shutil.copytree(candidate, destination, ignore=shutil.ignore_patterns('ACCEPTANCE.json', 'RELEASE-STATUS.txt'))
    p.require(p.validate_payload(destination) == (commit, digest), 'Staged installer payload differs from the SDK candidate.')
    return commit, digest


def record(folder, compiler, prerequisite, tool_report):
    meta = p.identity(); setup = folder/meta['setup_name']; payload = folder/'payload'
    p.require(not (folder/'build-receipt.json').exists(), 'Installer build receipt already exists.')
    pe(setup); pe(prerequisite)
    commit, digest = p.validate_payload(payload)
    report = p.read_json(tool_report)
    p.require(report.get('vc_signature') == 'Valid' and 'Microsoft Corporation' in report.get('vc_publisher', '')
              and report.get('vc_sha256') == sha(prerequisite) and report.get('vc_version') and report.get('vc_architecture') == 'x64', 'A signed Microsoft Visual C++ prerequisite matching the bundled file is required.')
    p.require(report.get('compiler_sha256') == sha(compiler) and report.get('compiler_version'), 'Inno compiler evidence is missing or differs.')
    report = {key: report[key] for key in ('compiler_sha256', 'compiler_version', 'vc_sha256', 'vc_version', 'vc_signature', 'vc_publisher', 'vc_architecture')}
    receipt = dict(format_version=1, version=meta['version'], source_commit=commit, payload_sha256=digest,
                   setup_name=setup.name, setup_sha256=sha(setup), compiler=report,
                   recipe_sha256=recipe_sha(),
                   files={name: sha(path) for name, path in sorted(p.files(payload).items())})
    write_json(folder/'build-receipt.json', receipt)
    p.write_checksum(setup)
    acceptance = p.acceptance_template(commit, digest)
    acceptance.update(installer_sha256=sha(setup), installer_receipt_sha256=sha(folder/'build-receipt.json'))
    write_json(folder/'ACCEPTANCE.json', acceptance)
    return receipt


def validate_candidate(folder, acceptance_path=None):
    meta = p.identity(); setup = folder/meta['setup_name']; receipt_path = folder/'build-receipt.json'
    receipt = p.read_json(receipt_path); pe(setup)
    p.require(receipt.get('format_version') == 1 and receipt.get('version') == meta['version']
              and receipt.get('setup_name') == setup.name and receipt.get('setup_sha256') == sha(setup), 'Installer differs from its build receipt.')
    p.require(setup.with_name(setup.name+'.sha256').read_text(encoding='utf-8').strip() == sha(setup)+'  '+setup.name, 'Installer checksum mismatch.')
    payload = folder/'payload'; commit, digest = p.validate_payload(payload)
    p.require(receipt.get('source_commit') == commit and receipt.get('payload_sha256') == digest
              and receipt.get('files') == {name: sha(path) for name, path in sorted(p.files(payload).items())}, 'Installer payload or provenance changed after compilation.')
    p.require(receipt.get('recipe_sha256') == recipe_sha(), 'Use the installer source revision that built this candidate; the recipe changed.')
    if acceptance_path:
        acceptance = p.validate_acceptance(acceptance_path, meta['version'], commit, digest)
        p.require(acceptance.get('installer_sha256') == sha(setup)
                  and acceptance.get('installer_receipt_sha256') == sha(receipt_path), 'Acceptance is not for this exact installer/build receipt.')
        return receipt, acceptance
    return receipt


def verify_installed(folder, installed):
    receipt = validate_candidate(folder)
    for name, digest in receipt['files'].items():
        file = installed/name
        p.require(file.is_file() and not file.is_symlink() and sha(file) == digest, f'Installed file missing or changed: {name}')
    p.require(not (installed/'missions.db').exists() and not (installed/'settings.json').exists(), 'User data must remain outside the application installation.')
    return receipt


def finalize(folder, acceptance_path, output):
    receipt, acceptance = validate_candidate(folder, acceptance_path)
    meta = p.identity(); output.mkdir(parents=True, exist_ok=True)
    names = (meta['setup_name'], meta['setup_name']+'.sha256', meta['setup_record_name'], meta['asset_name'], meta['asset_name']+'.sha256')
    p.require(not any((output/name).exists() for name in names), 'Final release files already exist; nothing was overwritten.')
    with tempfile.TemporaryDirectory(prefix='aeroscience-installer-final-', dir=output) as work:
        temporary = Path(work)
        p.finalize_candidate(folder/'payload', acceptance_path, temporary/meta['asset_name'])
        shutil.copy2(folder/meta['setup_name'], temporary/meta['setup_name'])
        p.write_checksum(temporary/meta['setup_name'])
        write_json(temporary/meta['setup_record_name'], dict(format_version=1, build=receipt, acceptance=acceptance,
                   installer_receipt_sha256=sha(folder/'build-receipt.json')))
        info = verify(temporary/meta['setup_name'], temporary/meta['setup_record_name'], temporary/meta['asset_name'])
        for name in names: (temporary/name).rename(output/name)
    (output/'RELEASE-NOTES.md').write_text(p.release_notes(info), encoding='utf-8')
    return info


def verify(setup, record_path, archive, expected_commit=None):
    if p.read_json(record_path).get('format_version')==2:
        from automatic_release import verify as verify_automatic
        return verify_automatic(setup,record_path,archive,expected_commit)
    info = p.verify_archive(archive, archive.with_name(archive.name+'.sha256'))
    record = p.read_json(record_path); build = record['build']; acceptance = record['acceptance']
    p.require(record.get('format_version') == 1 and setup.name == p.identity()['setup_name']
              and build.get('setup_name') == setup.name and build.get('version') == info['version'], 'Unexpected installer release identity.')
    pe(setup)
    p.require(build.get('setup_sha256') == sha(setup) == acceptance.get('installer_sha256'), 'Installer bytes differ from tested acceptance.')
    p.require(setup.with_name(setup.name+'.sha256').read_text(encoding='utf-8').strip() == sha(setup)+'  '+setup.name, 'Installer SHA-256 sidecar mismatch.')
    p.require(build.get('source_commit') == info['source_commit'] and build.get('payload_sha256') == info['payload_sha256']
              and acceptance == info['acceptance'] and record.get('installer_receipt_sha256') == acceptance.get('installer_receipt_sha256')
              and hashlib.sha256((json.dumps(build, sort_keys=True, indent=2)+'\n').encode()).hexdigest() == record['installer_receipt_sha256'], 'Installer receipt, source or acceptance binding differs.')
    if expected_commit: p.require(info['source_commit'] == expected_commit, 'Installer differs from selected source commit.')
    return {**info, 'distribution': 'installer', 'portable_asset_name': info['asset_name'], 'asset_name': setup.name, 'sha256': sha(setup)}


def main():
    parser = argparse.ArgumentParser(description=__doc__); sub = parser.add_subparsers(dest='command', required=True)
    staging = sub.add_parser('stage')
    for name in ('candidate', 'destination'): staging.add_argument('--'+name, type=Path, required=True)
    staging.add_argument('--expected-commit', required=True)
    recording = sub.add_parser('record')
    for name in ('folder', 'compiler', 'prerequisite', 'tool-report'): recording.add_argument('--'+name, type=Path, required=True)
    final = sub.add_parser('finalize')
    for name in ('folder', 'acceptance', 'output'): final.add_argument('--'+name, type=Path, required=True)
    verified = sub.add_parser('verify')
    for name in ('setup', 'record', 'archive'): verified.add_argument('--'+name, type=Path, required=True)
    verified.add_argument('--expected-commit'); verified.add_argument('--notes-output', type=Path)
    installed = sub.add_parser('verify-installed')
    for name in ('folder', 'installed'): installed.add_argument('--'+name, type=Path, required=True)
    args = parser.parse_args()
    if args.command == 'stage': result = stage(args.candidate, args.destination, args.expected_commit)
    elif args.command == 'record': result = record(args.folder, args.compiler, args.prerequisite, args.tool_report)
    elif args.command == 'finalize': result = finalize(args.folder, args.acceptance, args.output)
    elif args.command == 'verify-installed': result = verify_installed(args.folder, args.installed)
    else:
        result = verify(args.setup, args.record, args.archive, args.expected_commit)
        if args.notes_output: args.notes_output.write_text(p.release_notes(result), encoding='utf-8')
    print(json.dumps(result))


if __name__ == '__main__':
    try: main()
    except (ValueError, OSError, KeyError, json.JSONDecodeError) as error: raise SystemExit(f'Installer validation refused: {error}')
