"""Private 0.8 native validation only. No installation, release, or source download."""
import argparse
import datetime as dt
import hashlib
import json
import ntpath
import os
from pathlib import Path, PurePosixPath, PureWindowsPath
import platform
import re
import shutil
import stat
import struct
import subprocess
import sys
import tempfile
import uuid

ROOT = Path(__file__).resolve().parents[2]
BASELINE = 'd50b3ea9c50e5e6e561aa77b2daaac4ad0864e5f'
PACKAGE = 'aeroscience-validation-v0-8-0-toolbar'
PANEL = 'AeroScienceValidationV080'
TITLE = 'AeroScience 0.8.0 Validation'
RECEIPT = 'VALIDATION-RECEIPT.json'
LOCALES = ['en-US', 'es-ES', 'fr-FR', 'de-DE', 'pt-BR']
TESTS = {
    'isolation': 'Existing installations, Community packages, settings and original archives unchanged',
    'startup': 'Actual Windows companion launch, shared mutex and isolated profile/port/cookie',
    'sdk-toolbar': 'Genuine SDK toolbar opens with this source/attempt and SimConnect/CommBus',
    'panel-lifecycle': 'Close/reopen, drag, resize, 320x360, larger layout, scrolling and focus',
    'recording-only': 'Record a real flight without science, downloads or sharing',
    'science-lifecycle': 'Arm, start, pause while recording, resume, multiple sessions, end',
    'configuration-timing': 'Change interval during collection; confirm new configuration and fresh assessments, original evidence unchanged',
    'research-cards': 'Eligible focus/activation/attempt distinctions and original card identities',
    'stale-foreign': 'Stale/disconnected telemetry and foreign source/attempt/revision rejected',
    **{'language-'+v: 'Actual companion/dashboard/Coherent glyphs, labels, scroll, fallback and no commands on language change: '+v for v in LOCALES},
    'preferences': 'Language persistence, local browser override, Auto/Light/Dark and independent units',
    'accessibility': 'Actual High Contrast, keyboard, 200% text and screen reader where available',
    'restart-recovery': 'Restart/reconnect and reopen saved flight/session/original evidence',
    'source-consent': 'Explicit bounded source permission, failed-region retry versus Continue and holds',
    'sentinel-retained': 'Genuine Sentinel-2 retained metadata/reuse and original hashes',
    'landsat': 'Genuine Landsat metadata, dates and unavailable-source handling',
    'dem': 'Genuine Copernicus/USGS raster, QA/nodata, datum and supported gaps',
    'ept': 'Explicitly approved bounded public EPT, original project/date/CRS/attributes',
    'local-las-laz': 'Local genuine LAS/LAZ, pinned native helper and unchanged coordinates/attributes',
    'results': 'Actual corridor/terrain/LiDAR Results and original-versus-derived evidence',
    'privacy-storage': 'No implicit downloads, sharing, submissions, migrations or original-source mutation',
    'leave-validation': 'Close validation and explicitly return to existing companion; no automatic uninstall',
}

def require(value, message):
    if not value:
        raise ValueError(message)

def read(path):
    return json.loads(Path(path).read_text(encoding='utf-8-sig'))

def sha(path):
    with Path(path).open('rb') as f:
        digest=hashlib.sha256()
        for block in iter(lambda:f.read(1024*1024),b''):digest.update(block)
        return digest.hexdigest()

def utc():
    return dt.datetime.now(dt.timezone.utc).isoformat()

def write_new(path, value):
    with Path(path).open('x', encoding='utf-8') as f:
        json.dump(value, f, indent=2, ensure_ascii=False)
        f.write('\n')

def identities(commit, instance):
    require(isinstance(commit, str) and re.fullmatch('[0-9a-f]{40}', commit), 'Use an exact 40-character source SHA.')
    require(isinstance(instance, str) and re.fullmatch('[0-9a-f]{32}', instance) and int(instance, 16), 'Invalid private attempt identity.')

def command(args, **kwargs):
    return subprocess.run(list(map(str, args)), capture_output=True, text=True, check=True, timeout=kwargs.pop('timeout', 60), **kwargs).stdout.strip()

def source_check(repo, expected):
    identities(expected, '1'*32)
    repo = Path(repo).resolve()
    require(command(['git', '-C', repo, 'rev-parse', 'HEAD']) == expected, 'Source SHA mismatch; preserve the checkout and use the pinned worktree.')
    branch = command(['git', '-C', repo, 'branch', '--show-current'])
    require(branch in ('develop/v0.8.0', ''), 'Use develop/v0.8.0 or its explicitly pinned detached validation worktree.')
    require(not command(['git', '-C', repo, 'status', '--porcelain', '--untracked-files=all']), 'Dirty worktree; preserve local work. No build output was created.')
    require(command(['git','-C',repo,'rev-parse','--is-shallow-repository'])=='false', 'Fetch full Git source history before building; accepted P08a ancestry cannot be proved from a shallow checkout.')
    command(['git', '-C', repo, 'merge-base', '--is-ancestor', BASELINE, expected])
    return expected

def no_links(path):
    path = Path(path).absolute()
    require('..' not in path.parts and not str(path).startswith(('\\\\', '//')), 'Network/traversal paths are not supported.')
    for p in [path, *path.parents]:
        if p.exists() or p.is_symlink():
            require(not p.is_symlink() and not (getattr(p.lstat(), 'st_file_attributes', 0) & 0x400), 'Links/junctions/reparse points are forbidden.')
    return path

def overlap(a, b):
    # Windows comparisons remain case-insensitive, including in Linux policy tests.
    normalize = lambda p: ntpath.normpath(str(p)).replace('\\', '/').rstrip('/').casefold()
    a, b = normalize(a), normalize(b)
    return a == b or a.startswith(b+'/') or b.startswith(a+'/')

def protected_roots():
    roots = [Path(r'C:\Dev\AeroScience-release')]
    if os.name != 'nt':
        return roots
    local = Path(os.environ['LOCALAPPDATA'])
    roots += [local/'Programs/AeroScience', local/'AeroScience', *[local/('AeroScience-Validation-v'+v) for v in ['0.5.0','0.6.0','0.7.0','0.7.1','0.8.0']], *local.glob('AeroScience-Validation-*')]
    import winreg
    def registry(key, depth=0):
        if depth > 5:
            return
        try:
            with winreg.OpenKey(winreg.HKEY_CURRENT_USER, key) as handle:
                for name in ['InstallDirectory', 'MissionDirectory', 'ScienceLibraryDirectory', 'CommunityDirectory']:
                    try:
                        value, _ = winreg.QueryValueEx(handle, name)
                        if isinstance(value, str) and value:
                            roots.append(Path(value))
                    except FileNotFoundError:
                        pass
                for i in range(winreg.QueryInfoKey(handle)[0]):
                    registry(key+'\\'+winreg.EnumKey(handle, i), depth+1)
        except FileNotFoundError:
            pass
    registry(r'Software\AeroScience')
    # Read only directory fields, never credentials or original databases.
    for root in list(roots):
        for p in [root/'settings.json', *root.glob('*/settings.json')]:
            if p.is_file():
                require(not p.is_symlink(), 'Protected settings are linked; inspect paths locally before building.')
                settings = read(p)
                roots += [Path(settings[k]) for k in ['MissionDirectory', 'ScienceLibraryDirectory', 'CommunityDirectory'] if settings.get(k)]
    return roots

def safe_output(path, repo, sdk, protected=()):
    require(Path(path).is_absolute(), 'Use an absolute output path.')
    path = no_links(path)
    require(path.is_absolute() and path.name.startswith('Validation080-') and len(path.parts) >= 3, 'Choose a fresh absolute Validation080-<timestamp> directory outside installations.')
    require(not path.exists(), 'Output already exists; preserve this attempt and choose a new directory.')
    require(path.parent.is_dir(), 'Create/select the dedicated build parent first; no system folders are created automatically.')
    for ancestor in path.parents:
        if ancestor.name.casefold()=='aeroscience-builds':
            require(path.parent==ancestor, 'Use a direct fresh child of AeroScience-Builds; never nest in an existing tester/validator attempt.')
        require(not any((ancestor/n).exists() for n in ['AeroScience.exe',RECEIPT,'build-info.json']), 'Output is inside an existing tester installation or validation payload.')
    for root in [repo, sdk, *protected]:
        require(not overlap(path, root), 'Output overlaps a protected checkout, SDK, installation, Community or data directory.')
    require(not any(p.casefold() in ('community', 'community2024', 'windows', 'program files', 'program files (x86)') for p in path.parts), 'Output cannot be a simulator or system location.')
    return path

def inventory(folder, exclude=()):
    folder = no_links(folder)
    result, folded = {}, set()
    for parent, dirs, names in os.walk(folder, followlinks=False):
        for name in dirs+names:
            p = no_links(Path(parent)/name)
            if p.is_file():
                relative = p.relative_to(folder).as_posix()
                require(relative.casefold() not in folded, 'Case-colliding payload paths.')
                folded.add(relative.casefold())
                if relative not in exclude:
                    result[relative] = sha(p)
    return dict(sorted(result.items()))

def publication_paths(app, intermediate):
    for path in [app, intermediate]:
        require(Path(path).is_absolute(), 'Publication paths must be absolute.')
        path=no_links(path)
        require(not path.exists(), 'Preserve the existing publication attempt.')
        for root in [Path(r'C:\MSFS 2024 SDK'),*protected_roots()]:
            require(not overlap(path,root), 'Publication overlaps a protected installation or data root.')
        if overlap(path,ROOT):
            require(ROOT/'artifacts' in path.parents and 'native080-structural' in path.parts, 'Only the separate CI structural artifact lane may publish inside the checkout.')
        else:
            require(any(p.name.startswith('Validation080-') for p in path.parents), 'Use the fresh Validation080-* build attempt.')
    require(not overlap(app,intermediate), 'App and intermediate outputs must be separate sibling trees.')
    return True

def lidar_source():
    root = ROOT/'native/lidar'
    policy = read(root/'source-policy.json')
    found = inventory(root/'laszip')
    require(found == policy['files'], 'Pinned LASzip source tree differs from its original source policy.')
    source = hashlib.sha256(((root/'source-policy.json').read_text(encoding='utf-8')+sha(root/'reader.cpp')+sha(ROOT/'scripts/build/lidar_runtime.py')).encode()).hexdigest()
    return {'source': source, 'upstreamCommit': policy['commit'], 'policy_sha256': sha(root/'source-policy.json')}

def pe_x64(path, dll=False):
    b = Path(path).read_bytes()
    require(len(b) >= 64 and b[:2] == b'MZ', 'Expected a native Windows PE x64 image.')
    start = struct.unpack_from('<I', b, 60)[0]
    require(start+264 <= len(b) and b[start:start+4] == b'PE\0\0', 'Invalid native PE header.')
    machine, flags = struct.unpack_from('<H', b, start+4)[0], struct.unpack_from('<H', b, start+22)[0]
    require(machine == 0x8664 and bool(flags & 0x2000) == dll, 'Wrong Windows architecture or DLL/EXE kind.')
    require(struct.unpack_from('<H', b, start+24)[0] == 0x20b and struct.unpack_from('<I', b, start+24+112+14*8)[0] == 0, 'Managed wrapper or truncated native optional header refused.')

def preflight(repo, expected, sdk, approval, output, confirmed=False):
    checks = []
    def check(name, action, help_text):
        try:
            value = action()
            checks.append({'name': name, 'outcome': 'Passed', 'evidence': value})
        except (ValueError, OSError, subprocess.SubprocessError, KeyError, json.JSONDecodeError):
            checks.append({'name': name, 'outcome': 'Blocked', 'next_action': help_text})
    check('source', lambda: source_check(repo, expected), 'Use exact clean develop/v0.8.0 source or a pinned detached worktree with full Git history for P08a ancestry; do not reset existing work.')
    check('output-isolation', lambda: str(safe_output(output, repo, sdk, protected_roots())), 'Choose a new Validation080-* child of your dedicated builds folder, outside source/SDK/Community and all data roots.')
    check('Windows-x64', lambda: require(os.name == 'nt' and platform.machine().lower() in ('amd64', 'x86_64'), 'Windows x64 required'), 'Run locally on Windows x64. Remote Linux preparation is not native acceptance.')
    for name, args in [('PowerShell', ['pwsh', '-NoProfile', '-Command', '$PSVersionTable.PSVersion.ToString()']), ('Python', [sys.executable, '--version']), ('Git', ['git', '--version']), ('Node', ['node', '--version']), ('dotnet', ['dotnet', '--version'])]:
        def tool(name=name, args=args):
            text = command(args)
            if name == 'Node':
                require(int(text.lstrip('v').split('.')[0]) >= 22, 'Node 22+')
            if name == 'PowerShell':
                require(int(text.split('.')[0]) >= 7, 'PowerShell 7+')
            if name == 'Python':
                require(sys.version_info >= (3,11), 'Python 3.11+ required')
            if name == 'dotnet':
                require(text == read(Path(repo)/'global.json')['sdk']['version'], 'Pinned .NET SDK required')
            return text[:160]
        check(name, tool, 'Install/select '+name+' yourself; use the pinned global.json SDK, Node 22+ and PowerShell 7. No tools or licenses are installed by this script.')
    def locks():
        names = command(['git', '-C', repo, 'ls-files', '*packages.lock.json', 'package-lock.json']).splitlines()
        require(names and 'package-lock.json' in names, 'Dependency locks missing')
        return {n: sha(Path(repo)/n) for n in names}
    check('dependency-lock-inventory', locks, 'Restore the committed dependency locks. The build will require locked restore/npm ci without force-evaluate.')
    check('pinned-LiDAR-source', lidar_source, 'Restore the exact vendored LASzip policy/source, reader and license. No binary download is substituted.')
    def compiler():
        require(os.name == 'nt' and os.environ.get('VSCMD_ARG_TGT_ARCH', '').lower() == 'x64' and shutil.which('cl.exe') and shutil.which('dumpbin.exe'), 'x64 native build tools required')
        return {'target': 'x64', 'compiler': str(Path(shutil.which('cl.exe')).resolve()), 'dependency_tool': str(Path(shutil.which('dumpbin.exe')).resolve()), 'cmake': shutil.which('cmake'), 'cmake_required': False, 'build_method': 'Pinned direct cl.exe C++17 /MT build; CMake is not used.'}
    check('Visual-Studio-x64', compiler, 'Open the Visual Studio x64 Native Tools prompt, then run pwsh. Install the C++ x64 workload yourself. This decoder uses cl directly, not CMake.')
    def disk():
        path = safe_output(output, repo, sdk, protected_roots())
        free = shutil.disk_usage(path.parent).free
        require(free >= 12*1024**3, 'At least 12 GiB free required')
        with tempfile.TemporaryFile(dir=path.parent) as f:
            f.write(b'private-validation-write-probe')
        return {'free_bytes': free, 'minimum_bytes': 12*1024**3}
    check('disk-and-write', disk, 'Provide at least 12 GiB free in the dedicated build parent and write access. No previous attempt is removed.')
    def sdk_check():
        require(os.name == 'nt', 'Windows required')
        paths = [Path(sdk)/'Tools/bin/fspackagetool.exe', Path(sdk)/'SimConnect SDK/include/SimConnect.h', Path(sdk)/'SimConnect SDK/lib/SimConnect.dll']
        require(all(p.is_file() for p in paths), 'SDK files missing')
        pe_x64(paths[2], dll=True)
        return {p.name: sha(p) for p in paths}
    check('genuine-SDK-inputs', sdk_check, 'Select the actual installed MSFS 2024 SDK: fspackagetool.exe, SimConnect.h and native lib/SimConnect.dll. Managed wrappers/2020 SDKs are not substitutes.')
    def simulator_closed():
        require(os.name == 'nt', 'Windows required')
        running=command(['tasklist.exe','/FO','CSV','/NH']).casefold()
        require(not any('"'+name+'"' in running for name in ['flightsimulator2024.exe','flightsimulator.exe','fspackagetool.exe']), 'Simulator/package builder running')
        return 'MSFS/package builder closed; no process was terminated.'
    check('simulator-closed-for-build', simulator_closed, 'Close MSFS and any previous SDK package builder yourself before building; fspackagetool can attach and wait. No process is killed automatically.')
    def runtime_check():
        require(os.name == 'nt' and confirmed, 'Authorization confirmation required')
        runtime = Path(sdk)/'SimConnect SDK/lib/SimConnect.dll'
        with tempfile.TemporaryDirectory(prefix='AeroScience080-runtime-') as tmp:
            report = Path(tmp)/'runtime.json'
            command(['pwsh', '-NoProfile', '-File', ROOT/'scripts/build/Test-SimConnectRuntime.ps1', '-RuntimePath', runtime, '-ReportPath', report])
            from package_release import validate_runtime
            result, public = validate_runtime(runtime.parent, report, Path(approval))
            return {'report': result, 'public_authorization': public}
    check('native-runtime-loading-exports-authorization', runtime_check, 'Pass the existing hash-bound public runtime attestation and -RuntimeRedistributionConfirmed. Windows must actually load the native DLL and all required telemetry/CommBus exports. Keep correspondence private.')
    return {'format_version': 1, 'stage': 'preflight', 'source_commit': expected, 'checked_utc': utc(), 'ready': all(c['outcome'] == 'Passed' for c in checks), 'checks': checks, 'installed_acceptance': 'Not Tested', 'public_release_approved': False}

def begin(repo, commit, sdk, output, instance, proof):
    identities(commit, instance)
    source_check(repo, commit)
    path = safe_output(output, repo, sdk, protected_roots())
    require(proof.get('ready') is True and proof.get('source_commit') == commit, 'A successful matching local preflight is required.')
    path.mkdir()
    write_new(path/'preflight.json', proof)
    write_new(path/'BUILD-IN-PROGRESS.json', {'source_commit': commit, 'instance': instance, 'started_utc': utc(), 'completed': False})
    return path

def stage_toolbar(stage, commit, instance):
    identities(commit, instance)
    stage = no_links(stage)
    require(not stage.exists(), 'Preserve the earlier SDK stage; choose a new attempt.')
    shutil.copytree(ROOT/'msfs-toolbar', stage)
    panel = stage/'PackageSources/html_ui/InGamePanels/AeroScience'
    for name in ['ProofOfConcept.html', 'ProofOfConcept.js', 'WindowProbe.html', 'WindowProbe.js']:
        (panel/name).unlink()
    shutil.copyfile(ROOT/'src/AeroScience.Engine/wwwroot/geographic-view.js', panel/'AeroScience.Map.js')
    geography = ROOT/'src/AeroScience.Engine/wwwroot/geography'
    (panel/'AeroScience.Geography.js').write_text('\n'.join('window.AeroScience'+var+' = '+(geography/(name+'.geojson')).read_text(encoding='utf-8')+';' for name, var in [('countries', 'Countries'), ('places', 'Places'), ('airports', 'Airports')]), encoding='utf-8', newline='\n')
    shutil.copyfile(geography/'provenance.json', panel/'geography-provenance.json')
    changes = [('AeroScience.Command.v1', 'AeroScience.Validation.0_8_0.Command.v1'), ('AeroScience.Reply.v1', 'AeroScience.Validation.0_8_0.Reply.v1'), ('PANEL_AEROSCIENCE', 'PANEL_AEROSCIENCE_VALIDATION_V080'), ('ICON_AEROSCIENCE', 'ICON_AEROSCIENCE_VALIDATION_V080'), ('ingamepanel-aeroscience', 'ingamepanel-aeroscience-validation-v080'), ('IngamePanelAeroScience', 'IngamePanelAeroScienceValidationV080'), ('aeroscience_panel', 'aeroscience_validation_v080_panel'), ('InGamePanels/AeroScience/', 'InGamePanels/'+PANEL+'/'), ('aeroscience-toolbar.xml', PACKAGE+'.xml'), ('<Title>AeroScience</Title>', '<Title>'+TITLE+'</Title>'), ('Name="AeroScience"', 'Name="'+TITLE+'"'), ('title="AeroScience"', 'title="'+TITLE+'"')]
    for path in stage.rglob('*'):
        if path.is_file() and path.suffix in {'.xml', '.js', '.html', '.css', '.json'}:
            text = path.read_text(encoding='utf-8-sig')
            for old, new in changes:
                text = text.replace(old, new)
            if path.name == 'AeroScience.js':
                marker = 'const AEROSCIENCE_TOOLBAR_IDENTITY = { profile: "production", source: null };'
                require(marker in text and 'Object.assign({protocol:1,requestId,op}' in text and "reply.error.indexOf('Validation toolbar source mismatch')===0" in text, 'Native bootstrap/source-rejection/command marker changed; review staging before building.')
                text = text.replace(marker, 'const AEROSCIENCE_TOOLBAR_IDENTITY = '+json.dumps({'profile': 'validation', 'source': commit, 'instance': instance})+';')
                require('value1:AEROSCIENCE_TOOLBAR_IDENTITY.profile' in text, 'Native build-label marker changed; review attempt display.')
                text = text.replace('value1:AEROSCIENCE_TOOLBAR_IDENTITY.profile', 'value1:AEROSCIENCE_TOOLBAR_IDENTITY.profile+" 0.8.0 | attempt '+instance[:8]+'"')
                text = text.replace('AEROSCIENCE_TOOLBAR_IDENTITY', 'AEROSCIENCE_VALIDATION_V080_TOOLBAR_IDENTITY')
                text = text.replace('Object.assign({protocol:1,requestId,op}', 'Object.assign({protocol:1,requestId,op,validationSource:"'+commit+'",validationInstance:"'+instance+'"}')
            if path.name == 'toolbar-compatibility.json':
                value = json.loads(text)
                value.update(applicationVersion='0.8.0', minimumCompanionVersion='0.8.0', profile='validation', sourceCommit=commit, validationInstance=instance)
                text = json.dumps(value)
            if path.name == 'AeroScience.html':
                text = text.replace('Toolbar build not initialized.', TITLE+' | '+commit+' | attempt '+instance+' | initialization pending.')
            if path.name == 'aeroscience-toolbar.xml':
                text = text.replace('AssetPackage Version="0.7.0"', 'AssetPackage Version="0.8.0"')
            path.write_text(text, encoding='utf-8', newline='\n')
    for old, new in [('PackageDefinitions/aeroscience-toolbar.xml', 'PackageDefinitions/'+PACKAGE+'.xml'), ('PackageSources/InGamePanels/aeroscience_panel.xml', 'PackageSources/InGamePanels/aeroscience_validation_v080_panel.xml'), ('PackageSources/html_ui/InGamePanels/AeroScience', 'PackageSources/html_ui/InGamePanels/'+PANEL), ('PackageSources/html_ui/icons/toolbar/ICON_AEROSCIENCE.svg', 'PackageSources/html_ui/icons/toolbar/ICON_AEROSCIENCE_VALIDATION_V080.svg')]:
        (stage/old).rename(stage/new)

def validate_community(folder, commit, instance):
    identities(commit, instance)
    found = inventory(folder)
    indexed = {n.casefold(): n for n in found}
    require({'manifest.json', 'layout.json'} <= indexed.keys(), 'Missing genuine SDK manifest/layout output.')
    manifest = read(folder/indexed['manifest.json'])
    require(manifest.get('title') == TITLE and manifest.get('package_version') == '0.8.0' and manifest.get('dependencies') == [] and str(manifest.get('content_type', '')).upper() == 'MISC', 'Wrong private toolbar title/version/dependencies.')
    require(re.fullmatch(r'\d+\.\d+\.\d+(?:\.\d+)?', manifest.get('minimum_game_version', '')), 'Invalid SDK game version.')
    declared = set()
    for entry in read(folder/indexed['layout.json']).get('content', []):
        name = entry.get('path', '').replace('\\', '/')
        p = PurePosixPath(name)
        require(name and not p.is_absolute() and not PureWindowsPath(name).drive and '..' not in p.parts and p.as_posix() == name, 'Unsafe SDK layout path.')
        key = name.casefold()
        require(key not in declared and key in indexed and key != 'layout.json', 'Duplicate/missing SDK layout entry.')
        require(type(entry.get('size')) is int and (folder/indexed[key]).stat().st_size == entry['size'] and type(entry.get('date')) is int and entry['date'] > 0, 'SDK layout size/date differs.')
        declared.add(key)
    require(set(indexed)-{'manifest.json', 'layout.json'} <= declared, 'Undeclared SDK package file.')
    panel = 'html_ui/InGamePanels/'+PANEL+'/'
    required = ['InGamePanels/aeroscience_validation_v080_panel.spb', 'html_ui/icons/toolbar/ICON_AEROSCIENCE_VALIDATION_V080.svg']+[panel+n for n in ['AeroScience.html', 'AeroScience.js', 'AeroScience.css', 'AeroScience.Map.js', 'AeroScience.Geography.js', 'geography-provenance.json', 'AeroScience.I18n.js', 'AeroScience.LocaleData.js', 'AeroScience.LocaleFormat.js', 'AeroScience.FlightStatus.js', 'toolbar-compatibility.json']]
    require({n.casefold() for n in required} <= indexed.keys(), 'Missing complete native toolbar/locale/registration output.')
    actual = lambda n: folder/indexed[n.casefold()]
    registration = actual(required[0]).read_bytes()
    require(len(registration) > 32 and not registration.lstrip().startswith((b'<', b'{')), 'Source/placeholder registration is not a genuine SDK SPB.')
    descriptor = read(actual(panel+'toolbar-compatibility.json'))
    require(all(descriptor.get(k) == v for k, v in {'profile': 'validation', 'applicationVersion': '0.8.0', 'minimumCompanionVersion': '0.8.0', 'protocol': 1, 'sourceCommit': commit, 'validationInstance': instance}.items()), 'Stale/mismatched toolbar source/attempt/protocol.')
    script = actual(panel+'AeroScience.js').read_text(encoding='utf-8-sig')
    require(all(v in script for v in ['AeroScience.Validation.0_8_0.Command.v1', 'AeroScience.Validation.0_8_0.Reply.v1', commit, instance]) and all(v not in script for v in ['AeroScience.Command.v1', 'AeroScience.Reply.v1', 'AeroScience.Validation.0_7_0.']), 'Foreign CommBus identity in native toolbar.')
    require('panel-id="PANEL_AEROSCIENCE_VALIDATION_V080"' in actual(panel+'AeroScience.html').read_text(encoding='utf-8-sig'), 'Native TemplateElement panel identity missing.')
    require(sha(actual(required[1])) == sha(ROOT/'assets/branding/orbital-arc-toolbar-white.svg'), 'Native icon differs from the approved source.')
    return found

def decoder_probe(executable):
    """Decode the already-retained genuine historical tile; no download or source mutation."""
    require(command([executable,'--identity'],timeout=5)=='AS-LIDAR-1 '+lidar_source()['source'], 'Native decoder executable identity differs from pinned source.')
    fixture = ROOT/'tests/AeroScience.Tests/LidarOriginals'
    sample = fixture/'ia-fullstate-root.laz'
    reference = read(fixture/'independent-reference.json')
    require(sha(sample) == reference['originalSha256'], 'Original genuine LAZ fixture hash changed.')
    digest = hashlib.sha256()
    for first in range(0, reference['pointsCompared'], 4096):
        count = min(4096, reference['pointsCompared']-first)
        result = subprocess.run([str(executable), '--read-v1', str(sample), str(first), str(count)], check=True, capture_output=True, timeout=30)
        require(result.stdout[:8] == b'ASLIDAR1' and struct.unpack_from('<I', result.stdout, 8)[0] == count, 'Native decoder output protocol/count mismatch.')
        digest.update(result.stdout[12:])
    require(digest.hexdigest() == reference['nativeProtocolSha256'], 'Genuine native coordinates/attributes differ from the independent recorded decoder reference.')
    return {'platform': platform.system(), 'source_sha256': sha(sample), 'metadata_sha256': sha(fixture/'ia-fullstate-ept.json'), 'reference_sha256': sha(fixture/'independent-reference.json'), 'points': reference['pointsCompared'], 'raw_protocol_sha256': digest.hexdigest(), 'source': 'Retained IA_FullState public historical EPT root tile; sparse delivered subset. Dates/vertical datum unknown. No current-provider or in-flight acquisition acceptance.'}

def catalogs_check(folder, package, embedded):
    manifest = read(folder/'wwwroot/i18n/locale-manifest.json')
    require(manifest == read(ROOT/'src/AeroScience.Engine/wwwroot/i18n/locale-manifest.json'), 'Stale/mismatched dashboard catalog.')
    require([r['id'] for r in manifest['locales']] == LOCALES, 'Unexpected locale set.')
    native_text = (package/('html_ui/InGamePanels/'+PANEL+'/AeroScience.LocaleData.js')).read_text(encoding='utf-8-sig')
    data = json.JSONDecoder().raw_decode(native_text[native_text.index('=')+1:].lstrip())[0]
    require(data['manifest'] == manifest, 'Native/companion catalog identity differs.')
    for name,relative in [('manifest','locale-manifest.json'),('schema','message-schema.json')]+[(v,'locales/'+v+'.json') for v in LOCALES]:
        path=folder/'wwwroot/i18n'/relative
        require(sha(path)==sha(ROOT/'src/AeroScience.Engine/wwwroot/i18n'/relative) and embedded.get(name)==sha(path), 'Bundled/embedded locale bytes differ from this source.')
    require(sha(folder/'wwwroot/i18n/catalog.generated.js')==sha(ROOT/'src/AeroScience.Engine/wwwroot/i18n/catalog.generated.js'), 'Stale browser locale module.')
    return manifest

def payload_checks(folder, commit, instance):
    identities(commit, instance)
    found = inventory(folder, (RECEIPT,))
    required = {'AeroScience.exe', 'AeroScience.dll', 'AeroScience.Engine.dll', 'AeroScience.Missions.dll', 'SimConnect.dll', 'coreclr.dll', 'hostpolicy.dll', 'System.Windows.Forms.dll', 'build-info.json', 'compiled-profile.json', 'runtime-check.json', 'RUNTIME-AUTHORIZATION.json', 'LidarRuntime/AeroScience.LidarReader.exe', 'LidarRuntime/build.json', 'LidarRuntime/LASzip.txt', 'LidarRuntime/NOTICE.md', 'lidar-check.json', 'native-dependencies.json', 'wwwroot/i18n/locale-manifest.json', 'Toolbar/sdk-build.json', 'ACCEPTANCE-TEMPLATE.json'}
    require(required <= found.keys(), 'Incomplete private Windows payload.')
    for name in found:
        require(not name.casefold().endswith(('.eml', '.msg', '.mbox', '.pst', '.ost', '.pfx', '.dmp', '.db', '.sqlite')), 'Private credentials/archive/database content cannot ship in the validation package.')
        if name.casefold().endswith('.exe'):
            require(name in {'AeroScience.exe', 'LidarRuntime/AeroScience.LidarReader.exe'}, 'Unexpected executable substitution/developer apphost.')
    for name in ['AeroScience.exe', 'LidarRuntime/AeroScience.LidarReader.exe']:
        pe_x64(folder/name)
    pe_x64(folder/'SimConnect.dll', dll=True)
    info, profile = read(folder/'build-info.json'), read(folder/'compiled-profile.json')
    for data in (info, profile):
        require(data.get('profile') == 'validation' and data.get('version') == '0.8.0' and data.get('source_commit') == commit and data.get('instance') == instance, 'Mismatched private source/build identity.')
    require(info.get('source_dirty') is False and info.get('public_release_approved') is False, 'Private clean-source/non-release identity missing.')
    require(profile.get('engine_sha256') == found['AeroScience.Engine.dll'] and profile.get('port') == 5088 and profile.get('mutex') == r'Local\AeroScience.Desktop', 'Published Engine/profile identity mismatch.')
    dependencies=read(folder/'native-dependencies.json')
    require(dependencies.get('format_version')==1 and dependencies.get('public_signing_approval') is False, 'Private native dependency/signing projection missing.')
    for name in ['SimConnect.dll','LidarRuntime/AeroScience.LidarReader.exe']:
        dependency=dependencies.get('files',{}).get(name,{})
        require(dependency.get('sha256')==found[name] and isinstance(dependency.get('imports'),list) and 1<=len(dependency['imports'])<=128 and all(re.fullmatch(r'[A-Za-z0-9_.-]+\.dll',n,re.I) for n in dependency['imports']) and dependency.get('signature_status') in ['Valid','NotSigned'], 'Native dependency inventory differs from payload or reports an invalid signature.')
    require(PureWindowsPath(profile['configuration_directory']).parts[-2:] == ('AeroScience-Validation-v0.8.0', instance), 'Private configuration directory is not attempt-confined.')
    source = lidar_source()
    stamp = read(folder/'LidarRuntime/build.json')
    require(stamp.get('source') == source['source'] and stamp.get('upstreamCommit') == source['upstreamCommit'] and stamp.get('sha256') == found['LidarRuntime/AeroScience.LidarReader.exe'], 'Incorrect native LiDAR helper/source stamp.')
    require(profile.get('lidar_source') == source['source'] and profile.get('lidar_sha256') == stamp['sha256'], 'Native helper differs from the identity embedded in published Missions.dll.')
    require(sha(folder/'LidarRuntime/LASzip.txt') == sha(ROOT/'native/lidar/laszip/COPYING.txt') and sha(folder/'LidarRuntime/NOTICE.md') == sha(ROOT/'licenses/AeroScience.LidarReader.NOTICE.md'), 'Native source license/NOTICE mismatch.')
    decoder = read(folder/'lidar-check.json')
    require(decoder.get('platform') == 'Windows' and decoder.get('points') == 34923 and decoder.get('raw_protocol_sha256') == read(ROOT/'tests/AeroScience.Tests/LidarOriginals/independent-reference.json')['nativeProtocolSha256'], 'Actual Windows genuine retained LAZ decode result required.')
    from package_release import validate_runtime
    validate_runtime(folder, folder/'runtime-check.json', folder/'RUNTIME-AUTHORIZATION.json')
    package = folder/'Toolbar'/PACKAGE
    assets = validate_community(package, commit, instance)
    sdk = read(folder/'Toolbar/sdk-build.json')
    require(sdk.get('variant') == 'toolbar-validation-080' and sdk.get('source_commit') == commit and sdk.get('instance') == instance and sdk.get('source_dirty') is False and sdk.get('sdk_contract') == 'passed' and sdk.get('files') == assets, 'Unmatched/missing genuine SDK build receipt.')
    require(all(re.fullmatch('[0-9a-f]{64}', sdk.get(k, '')) for k in ['builder_sha256', 'sdk_header_sha256']), 'SDK input hashes missing.')
    from private_validation import validate_native_javascript
    validate_native_javascript(sdk.get('native_javascript'), assets)
    # Parse generated native JSON only; never execute package scripts in the verifier.
    manifest = catalogs_check(folder,package,profile.get('presentation_resources',{}))
    template = read(folder/'ACCEPTANCE-TEMPLATE.json')
    require(template.get('instance') == instance and template.get('source_commit') == commit and set(template.get('tests', {})) == set(TESTS) and all(v.get('outcome') == 'Not Tested' for v in template['tests'].values()), 'Generated manual form must not claim any observed acceptance.')
    return found, manifest, source

def acceptance_template(commit, instance):
    identities(commit, instance)
    return {'format_version': 1, 'source_commit': commit, 'instance': instance, 'public_release_approved': False, 'qualified_human_translation_review': 'Pending', 'tests': {key: {'name': name, 'outcome': 'Not Tested', 'observations': '', 'evidence': []} for key, name in TESTS.items()}}

def record(folder, commit, instance):
    require(os.name == 'nt', 'Final native package recording requires actual Windows execution. Linux fixtures are not acceptance.')
    require(not (folder/RECEIPT).exists(), 'Preserve the existing receipt; choose a fresh build attempt.')
    found, manifest, source = payload_checks(folder, commit, instance)
    receipt = {'format_version': 1, 'profile': 'validation', 'version': '0.8.0', 'source_commit': commit, 'instance': instance, 'recorded_utc': utc(), 'public_release_approved': False, 'installed_acceptance': 'Not Tested', 'catalog': manifest, 'lidar': source, 'files': found}
    write_new(folder/RECEIPT, receipt)
    return sha(folder/RECEIPT)

def verify(folder, expected, receipt_hash=None):
    folder = no_links(folder)
    value = read(folder/RECEIPT)
    identities(expected, value.get('instance'))
    require(value.get('format_version') == 1 and value.get('source_commit') == expected and value.get('profile') == 'validation' and value.get('version') == '0.8.0' and value.get('public_release_approved') is False and value.get('installed_acceptance') == 'Not Tested', 'Stale/foreign/private validation receipt.')
    if receipt_hash:
        require(sha(folder/RECEIPT) == receipt_hash, 'Receipt hash differs from the independently retained build receipt.')
    require(value.get('files') == inventory(folder, (RECEIPT,)), 'Private package bytes changed; do not launch or install.')
    payload_checks(folder, expected, value['instance'])
    return value

def prepare_profile(folder, expected, local):
    receipt = verify(folder, expected)
    instance = receipt['instance']
    profile = no_links(Path(local)/'AeroScience-Validation-v0.8.0'/instance)
    require(overlap(profile, Path(local)/'AeroScience-Validation-v0.8.0'), 'Invalid local profile root.')
    if profile.exists():
        marker = read(profile/'validation-profile.json')
        require(marker == {'version': 1, 'source_commit': expected, 'instance': instance}, 'Existing validation profile belongs to another attempt; it was not changed.')
    else:
        profile.mkdir(parents=True)
        write_new(profile/'validation-profile.json', {'version': 1, 'source_commit': expected, 'instance': instance})
    # Refuse an edited configuration that points outside this disposable attempt.
    if (profile/'settings.json').exists():
        settings = read(profile/'settings.json')
        for key in ['MissionDirectory', 'ScienceLibraryDirectory']:
            selected = no_links(settings[key])
            require(selected == profile or profile in selected.parents, 'Configured validation data escaped its profile.')
    return str(profile)

def observation(folder, expected, results, test_id, outcome, notes, observed=False, evidence=None):
    receipt = verify(folder, expected)
    require(test_id in TESTS and outcome in ['Passed', 'Failed', 'Blocked', 'Not Tested'], 'Unknown acceptance test/outcome.')
    require(outcome != 'Passed' or observed and isinstance(notes, str) and notes.strip(), 'Passed requires explicit observed testing and a meaningful tester note.')
    require(isinstance(notes, str) and len(notes) <= 4000 and not re.search(r'(?i)bearer\s+\S+|password\s*[:=]|access_token\s*[:=]', notes), 'Do not put credentials in acceptance notes.')
    results = no_links(results)
    require(not overlap(results, folder), 'Acceptance results must stay outside the sealed application payload.')
    require(results.name == 'Results' and results.parent == Path(folder).parent, 'Use only this attempt\'s sibling Results directory.')
    results.mkdir(exist_ok=True)
    refs = []
    if evidence:
        p = no_links(evidence)
        require(results in p.parents and p.is_file(), 'Copy only reviewed non-secret evidence into this attempt\'s Results directory first.')
        refs = [{'file': p.relative_to(results).as_posix(), 'sha256': sha(p)}]
    event = {'format_version': 1, 'source_commit': expected, 'instance': receipt['instance'], 'validation_profile':{'version':'0.8.0','folder_namespace':'AeroScience-Validation-v0.8.0/'+receipt['instance'],'port':5088}, 'receipt_sha256': sha(Path(folder)/RECEIPT), 'catalog': receipt['catalog'], 'runtime_sha256': receipt['files']['SimConnect.dll'], 'native_lidar_sha256': receipt['files']['LidarRuntime/AeroScience.LidarReader.exe'], 'toolbar_sdk_receipt_sha256': receipt['files']['Toolbar/sdk-build.json'], 'test_id': test_id, 'test_name': TESTS[test_id], 'outcome': outcome, 'observed_utc': utc(), 'tester_observations': notes, 'evidence': refs, 'known_restrictions':['Private unsigned build, no public release approval','Historical source/date/datum limitations remain','Fluent translation review remains separate','P08c numerical/endurance acceptance not performed'], 'public_release_approved': False}
    path = results/(dt.datetime.now(dt.timezone.utc).strftime('%Y%m%dT%H%M%S')+'-'+uuid.uuid4().hex+'.json')
    write_new(path, event)
    return str(path)

def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('action', choices=['preflight', 'begin', 'publish-paths', 'stage-toolbar', 'verify', 'record', 'prepare-profile', 'observation', 'source-check', 'decoder-probe', 'template'])
    p.add_argument('--folder', type=Path);p.add_argument('--source');p.add_argument('--instance');p.add_argument('--sdk', type=Path);p.add_argument('--output', type=Path);p.add_argument('--approval', type=Path);p.add_argument('--confirmed', action='store_true');p.add_argument('--report', type=Path);p.add_argument('--receipt-hash');p.add_argument('--local', type=Path);p.add_argument('--results', type=Path);p.add_argument('--test-id');p.add_argument('--outcome');p.add_argument('--notes', default='');p.add_argument('--observed', action='store_true');p.add_argument('--evidence', type=Path)
    a = p.parse_args()
    try:
        if a.action == 'preflight':
            value = preflight(ROOT, a.source, a.sdk, a.approval, a.output, a.confirmed)
            if a.report:
                no_links(a.report);require(a.report.parent.absolute()==a.output.parent.absolute() and a.report.name.startswith('Preflight080-') and a.report.suffix=='.json', 'Write a new Preflight080-*.json only beside the proposed attempt, outside protected paths.')
                require(not a.report.exists(), 'Preserve the existing preflight report.')
                for root in [ROOT, a.sdk, *protected_roots()]:
                    require(not overlap(a.report, root), 'Preflight report overlaps a protected location.')
                write_new(a.report, value)
            # The native report/public attestation are consumed internally by the build, not printed in diagnostics.
            display = {**value, 'checks': [{k:v for k,v in row.items() if k != 'evidence' or row['name'] != 'native-runtime-loading-exports-authorization'} for row in value['checks']]}
            print(json.dumps(display, indent=2));return 0 if value['ready'] else 1
        if a.action == 'publish-paths':value=publication_paths(a.folder,a.output)
        elif a.action == 'begin':value = str(begin(ROOT, a.source, a.sdk, a.output, a.instance, read(a.report)))
        elif a.action == 'source-check':value = source_check(ROOT, a.source)
        elif a.action == 'stage-toolbar':stage_toolbar(a.folder, a.source, a.instance);value = {'staged': str(a.folder), 'installed_acceptance': 'Not Tested'}
        elif a.action == 'verify':value = {'verified': True, 'source_commit': verify(a.folder, a.source, a.receipt_hash)['source_commit'], 'installed_acceptance': 'Not Tested'}
        elif a.action == 'record':value = {'receipt_sha256': record(a.folder, a.source, a.instance)}
        elif a.action == 'prepare-profile':
            require(os.name == 'nt' and a.local.resolve()==Path(os.environ['LOCALAPPDATA']).resolve(), 'Owner-local Windows LOCALAPPDATA launch only.')
            value = prepare_profile(a.folder, a.source, a.local)
        elif a.action == 'decoder-probe':value = decoder_probe(a.folder)
        elif a.action == 'template':value = acceptance_template(a.source, a.instance)
        else:value = observation(a.folder, a.source, a.results, a.test_id, a.outcome, a.notes, a.observed, a.evidence)
        print(json.dumps(value, indent=2));return 0
    except (ValueError, OSError, KeyError, TypeError, subprocess.SubprocessError, json.JSONDecodeError) as e:
        # Never print command stderr or authorization file contents.
        print('Private validation refused: '+(str(e) if isinstance(e, ValueError) else type(e).__name__+'; inspect prerequisites and preserve this attempt.'), file=sys.stderr);return 1

if __name__ == '__main__':
    sys.exit(main())
