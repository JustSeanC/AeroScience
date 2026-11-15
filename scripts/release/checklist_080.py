"""Local checklist view over the existing private validation observation recorder."""
import argparse
from contextlib import contextmanager
import datetime as dt
import json
import os
from pathlib import Path, PurePosixPath, PureWindowsPath
import re
import subprocess
import sys

import private_validation_080 as validation

OUTCOMES = ['Not Tested', 'Passed', 'Failed', 'Blocked']
MAX_REQUEST_BYTES = 512 * 1024  # All bounded notes can include four-byte Unicode.
TITLES = {
    'isolation': 'My usual installation and files stay safe',
    'startup': 'The test companion opens correctly',
    'sdk-toolbar': 'The test toolbar connects inside MSFS',
    'panel-lifecycle': 'Move, resize, scroll and reopen the toolbar',
    'recording-only': 'Record a flight with science switched off',
    'science-lifecycle': 'Start, pause, resume and end science',
    'configuration-timing': 'Change the science collection interval',
    'research-cards': 'Choose and use research cards',
    'stale-foreign': 'Handle lost connections and mismatched builds',
    'language-en-US': 'English labels and controls',
    'language-es-ES': 'Spanish labels and controls',
    'language-fr-FR': 'French labels and controls',
    'language-de-DE': 'German labels and controls',
    'language-pt-BR': 'Brazilian Portuguese labels and controls',
    'preferences': 'Remember language, theme and units',
    'accessibility': 'Keyboard, larger text and accessibility',
    'restart-recovery': 'Restart and reopen saved test results',
    'source-consent': 'Ask before using or downloading source data',
    'sentinel-retained': 'Check genuine saved Sentinel-2 data',
    'landsat': 'Check genuine Landsat data',
    'dem': 'Check genuine elevation data',
    'ept': 'Check an explicitly approved historical EPT source',
    'local-las-laz': 'Check a genuine local LAS or LAZ file',
    'results': 'Check terrain and LiDAR results and their sources',
    'privacy-storage': 'Keep data and sharing under my control',
    'leave-validation': 'Return to my usual AeroScience companion',
}


def _latest(folder, source, receipt, receipt_hash):
    results = validation.no_links(folder.parent/'Results')
    latest = {}
    if not results.exists():
        return latest
    validation.require(results.is_dir(), 'The results location is not a directory.')
    files = sorted(results.glob('*.json'))
    validation.require(len(files) <= 2000, 'Too many results files; keep them and ask for help before continuing.')
    for path in files:
        # Other reviewed JSON evidence can coexist with recorder events.
        if not re.fullmatch(r'\d{8}T\d{6}-[0-9a-f]{32}\.json', path.name):
            continue
        validation.no_links(path)
        validation.require(path.stat().st_size <= 65536, 'An existing result is too large; preserve it and ask for help.')
        event = validation.read(path)
        validation.require(isinstance(event, dict), 'An existing checklist event is malformed; preserve it and ask for help.')
        if (event.get('source_commit'), event.get('instance'), event.get('receipt_sha256')) != (source, receipt['instance'], receipt_hash):
            continue
        key = event.get('test_id')
        validation.require(event.get('format_version') == 1 and key in validation.TESTS
            and event.get('test_name') == validation.TESTS[key] and event.get('outcome') in OUTCOMES
            and event.get('public_release_approved') is False, 'An existing result is not a recognized private checklist event.')
        for field, name in [('runtime_sha256', 'SimConnect.dll'), ('native_lidar_sha256', 'LidarRuntime/AeroScience.LidarReader.exe'), ('toolbar_sdk_receipt_sha256', 'Toolbar/sdk-build.json')]:
            validation.require(event.get(field) == receipt['files'][name], 'An existing result identifies different build files.')
        notes = event.get('tester_observations')
        validation.require(isinstance(notes, str) and len(notes) <= 4000
            and (event['outcome'] != 'Passed' or notes.strip()), 'An existing result has invalid test notes.')
        when = dt.datetime.fromisoformat(event['observed_utc'])
        validation.require(when.tzinfo is not None, 'An existing result is missing its observation timezone.')
        order = (when, path.name)
        if key not in latest or order > latest[key]['order']:
            latest[key] = {'event': event, 'order': order, 'revision': validation.sha(path)}
    return latest


def _state(folder, source, receipt, receipt_hash):
    validation.require(set(TITLES) == set(validation.TESTS), 'Checklist instructions need updating before use.')
    latest = _latest(folder, source, receipt, receipt_hash)
    rows = []
    for key, instructions in validation.TESTS.items():
        saved = latest.get(key)
        rows.append({'id': key, 'title': TITLES[key], 'instructions': instructions,
            'outcome': saved['event']['outcome'] if saved else 'Not Tested',
            'notes': saved['event']['tester_observations'] if saved else '',
            'revision': saved['revision'] if saved else None})
    return {'format_version': 1, 'source_commit': source, 'instance': receipt['instance'],
        'receipt_sha256': receipt_hash, 'results_directory': str(folder.parent/'Results'),
        'outcomes': OUTCOMES, 'rows': rows, 'public_release_approved': False,
        'qualified_human_translation_review': 'Pending'}


def load(folder, source, receipt_hash):
    folder = validation.no_links(folder)
    receipt = validation.verify(folder, source, receipt_hash)
    return _state(folder, source, receipt, receipt_hash)


@contextmanager
def _save_lock(results):
    results.mkdir(exist_ok=True)
    path = validation.no_links(results/'.checklist.lock')
    with path.open('a+b') as handle:
        if handle.tell() == 0:
            handle.write(b'0'); handle.flush()
        handle.seek(0)
        try:
            if os.name == 'nt':
                import msvcrt
                msvcrt.locking(handle.fileno(), msvcrt.LK_NBLCK, 1)
            else:
                import fcntl
                fcntl.flock(handle.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
        except OSError:
            raise ValueError('Another checklist is saving. Wait for it to finish, then try again.') from None
        try:
            yield
        finally:
            handle.seek(0)
            if os.name == 'nt':
                msvcrt.locking(handle.fileno(), msvcrt.LK_UNLCK, 1)
            else:
                fcntl.flock(handle.fileno(), fcntl.LOCK_UN)


def save(folder, source, receipt_hash, request):
    folder = validation.no_links(folder)
    receipt = validation.verify(folder, source, receipt_hash)
    validation.require(isinstance(request, dict) and set(request) == {'entries', 'observed'}
        and type(request['observed']) is bool and isinstance(request['entries'], list)
        and 1 <= len(request['entries']) <= len(validation.TESTS), 'Choose at least one changed check to save.')
    results = validation.no_links(folder.parent/'Results')
    with _save_lock(results):
        latest = _latest(folder, source, receipt, receipt_hash)
        seen, prepared = set(), []
        for row in request['entries']:
            validation.require(isinstance(row, dict) and set(row) == {'id', 'outcome', 'notes', 'revision'}, 'Unknown checklist fields.')
            key, outcome, notes = row['id'], row['outcome'], row['notes']
            validation.require(isinstance(key, str) and key in validation.TESTS and key not in seen and outcome in OUTCOMES, 'Choose each named check once, using a listed result.')
            seen.add(key)
            prior = latest.get(key)
            validation.require(row['revision'] == (prior['revision'] if prior else None), 'Newer notes were saved for '+TITLES[key]+'. Reopen the checklist before changing this check.')
            validation.require(isinstance(notes, str) and len(notes) <= 4000
                and not re.search(r'(?i)bearer\s+\S+|password\s*[:=]|access_token\s*[:=]', notes), 'Keep notes under 4,001 characters and leave credentials out.')
            validation.require(outcome != 'Passed' or request['observed'] and notes.strip(), 'For Passed, add what you observed and tick the personal-testing checkbox.')
            evidence = None
            refs = prior['event'].get('evidence', []) if prior else []
            validation.require(isinstance(refs, list) and len(refs) <= 1, 'Existing evidence references need review before editing this check.')
            if refs:
                name = refs[0]['file']; relative = PurePosixPath(name)
                validation.require(not relative.is_absolute() and not PureWindowsPath(name).drive and '..' not in relative.parts and '\\' not in name, 'Existing evidence must remain inside this attempt Results directory.')
                evidence = validation.no_links(results/relative)
                validation.require(evidence.is_file() and validation.sha(evidence) == refs[0]['sha256'], 'Existing evidence changed or is missing. Preserve your previous notes and review the evidence first.')
            prepared.append((key, outcome, notes, evidence))
        saved = []
        for key, outcome, notes, evidence in prepared:
            # Keep the original authoritative validator, identities, evidence checks,
            # per-event verification and append-only event format.
            saved.append(validation.observation(folder, source, results, key, outcome, notes,
                observed=request['observed'], evidence=evidence))
        return {'saved': len(saved), 'state': _state(folder, source, receipt, receipt_hash)}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('action', choices=['load', 'save'])
    parser.add_argument('--folder', type=Path, required=True)
    parser.add_argument('--source', required=True)
    parser.add_argument('--receipt-hash', required=True)
    args = parser.parse_args()
    try:
        validation.require(re.fullmatch('[0-9a-f]{64}', args.receipt_hash), 'Use the independently retained receipt SHA-256.')
        if args.action == 'load':
            value = load(args.folder, args.source, args.receipt_hash)
        else:
            raw = sys.stdin.buffer.read(MAX_REQUEST_BYTES + 1)
            validation.require(len(raw) <= MAX_REQUEST_BYTES, 'Checklist request is too large.')
            value = save(args.folder, args.source, args.receipt_hash, json.loads(raw.decode('utf-8')))
        print(json.dumps(value, ensure_ascii=True))
        return 0
    except (ValueError, OSError, KeyError, TypeError, subprocess.SubprocessError) as error:
        print(str(error) if isinstance(error, ValueError) else 'Checklist could not be saved. Preserve the files and reopen to see any notes already saved.', file=sys.stderr)
        return 1


if __name__ == '__main__':
    sys.exit(main())
