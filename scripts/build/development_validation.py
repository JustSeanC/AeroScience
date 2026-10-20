#!/usr/bin/env python3
"""Conservative development test selection. A targeted pass is never full/release acceptance."""
import argparse
import json
import os
from pathlib import Path
import subprocess
import unittest

ROOT = Path(__file__).resolve().parents[2]
CORE = {'EvidenceContractsTests', 'MissionEvidenceStoreTests', 'MissionTests', 'ModeAndToolbarTests', 'ApiTests'}
PORTABLE_ONLY = {'test_airport_search', 'test_toolbar_sources'}


def validation_tier(event, ref, base='', requested='full', message=''):
    if event == 'workflow_dispatch':
        if requested not in {'fast', 'full'}:
            raise ValueError('Unknown validation tier; refusing an empty validation run.')
        return requested
    development = ref == 'refs/heads/develop/v0.5.0' or base == 'develop/v0.5.0'
    return 'fast' if development and '[full-validation]' not in message else 'full'


def plan(paths, uncertain=False):
    dotnet = set(CORE)
    web = {'tests/web/dashboard.spec.js'}
    helpers = set()
    full_dotnet = uncertain
    for name in paths:
        path = Path(name)
        if name.startswith('tests/AeroScience.Tests/') and name.endswith('.cs'):
            if path.stem.endswith('Tests'):
                dotnet.add(path.stem)
            else:
                full_dotnet = True  # A fixture can affect many test classes.
        elif name.endswith(('.csproj', '.props', '.sln')) or name == 'global.json' or name.endswith('packages.lock.json'):
            full_dotnet = True
        elif name.startswith('src/') and name.endswith('.cs'):
            if path.name.startswith(('MissionEvidence', 'EvidenceContracts')):
                dotnet.update({'AlphaDataTests', 'ResearchTests', 'ScientificWorkspaceTests'})
            elif path.name == 'DataDirectoryLease.cs':
                dotnet.update({'OfflineApiTests', 'StorageTests'})
            else:
                full_dotnet = True  # Unknown/shared dependencies get the complete .NET suite.
        if name.startswith('tests/web/') and name.endswith('.spec.js'):
            web.add(name)
        if name.endswith('help-center.js'):
            web.add('tests/web/science-review-041.spec.js')
        elif name.endswith('.js') and ('planner' in name or 'pattern' in name):
            web.update({'tests/web/planner-units.spec.js', 'tests/web/planner-ux.spec.js'})
        elif name.endswith('.js') and ('science' in name or 'source' in name):
            web.add('tests/web/remote-science.spec.js')
        if name.startswith('tests/test_') and name.endswith('.py'):
            helpers.add(path.stem)
        elif name == 'scripts/build/development_validation.py' or name == '.github/workflows/ci.yml':
            helpers.add('test_development_validation')
        elif name.startswith('scripts/') and name.endswith(('.py', '.ps1')):
            helpers.add('*')  # Packaging/release/build scripts have transitive consumers.
    if uncertain:
        helpers.add('*')
        web = {'tests/web'}
    return {'scope': 'development-only; not full integration, genuine science or hardware acceptance',
            'changed': sorted(paths), 'dotnetFilter': None if full_dotnet else '|'.join('FullyQualifiedName~'+n for n in sorted(dotnet)),
            'browserSpecs': sorted(web), 'helperModules': sorted(helpers)}


def run_helpers(modules, windows=False):
    loader = unittest.TestLoader()
    suite = unittest.TestSuite()
    # Linux full owns the complete portable suite. Windows retains every other existing
    # and future module, including Git byte preservation, paths, PowerShell and installers.
    names = sorted(p.stem for p in (ROOT/'tests').glob('test_*.py')) if '*' in modules else modules
    for name in names:
        if windows and name in PORTABLE_ONLY:
            print(f'{name}: portable-only duplicate; covered by Linux full integration.', flush=True)
            continue
        suite.addTests(loader.discover(str(ROOT/'tests'), pattern=name+'.py'))
    result = unittest.TextTestRunner(verbosity=2).run(suite)
    if not result.wasSuccessful():
        raise SystemExit(1)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('action', choices=['tier', 'plan', 'dotnet', 'browser', 'helpers', 'windows-helpers'])
    parser.add_argument('--base', default='')
    parser.add_argument('--plan', type=Path, default=ROOT/'artifacts/development-plan.json')
    args = parser.parse_args()
    if args.action == 'tier':
        event = json.loads(Path(os.environ['GITHUB_EVENT_PATH']).read_text(encoding='utf-8'))
        tier = validation_tier(os.environ['GITHUB_EVENT_NAME'], os.environ['GITHUB_REF'],
                               os.environ.get('BASE_REF', ''), os.environ.get('REQUESTED') or 'full',
                               (event.get('head_commit') or {}).get('message', ''))
        with open(os.environ['GITHUB_OUTPUT'], 'a', encoding='utf-8') as stream:
            stream.write('tier='+tier+'\n')
        with open(os.environ['GITHUB_STEP_SUMMARY'], 'a', encoding='utf-8') as stream:
            stream.write('Validation tier: **'+tier+'**. Targeted development success is not full integration, genuine scientific or Windows/MSFS/device acceptance.\n')
        return
    if args.action == 'plan':
        diff = subprocess.run(['git', 'diff', '--name-only', args.base, 'HEAD'], cwd=ROOT, capture_output=True, text=True) if args.base and set(args.base) != {'0'} else None
        selected = plan(diff.stdout.splitlines() if diff and diff.returncode == 0 else [], uncertain=diff is None or diff.returncode != 0)
        args.plan.parent.mkdir(parents=True, exist_ok=True)
        args.plan.write_text(json.dumps(selected, indent=2)+'\n', encoding='utf-8')
        print(json.dumps(selected, indent=2))
        if os.environ.get('GITHUB_STEP_SUMMARY'):
            with open(os.environ['GITHUB_STEP_SUMMARY'], 'a', encoding='utf-8') as summary:
                summary.write('### Fast development scope\n\nTargeted checks only; full integration and release/hardware acceptance remain separate.\n\n```json\n'+json.dumps(selected, indent=2)+'\n```\n')
        return
    if args.action == 'windows-helpers':
        run_helpers(['*'], windows=True)
        return
    selected = json.loads(args.plan.read_text(encoding='utf-8'))
    if args.action == 'dotnet':
        command = ['dotnet', 'test', 'tests/AeroScience.Tests', '-c', 'Release', '--no-build', '--no-restore',
                   '--logger', 'trx;LogFileName=development.trx', '--logger', 'console;verbosity=normal',
                   '--blame-hang-timeout', '2m', '--blame-hang-dump-type', 'none']
        if selected['dotnetFilter']:
            command.extend(['--filter', selected['dotnetFilter']])
        subprocess.run(command, cwd=ROOT, check=True)
    elif args.action == 'browser':
        subprocess.run(['npm', 'run', 'test:web', '--', *selected['browserSpecs']], cwd=ROOT, check=True)
    else:
        run_helpers(selected['helperModules'])


if __name__ == '__main__':
    main()
