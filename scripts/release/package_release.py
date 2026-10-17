#!/usr/bin/env python3
"""Fail-closed packaging of real SDK outputs; never manufacture manifests or compiled panels."""
from __future__ import annotations
import argparse
from datetime import datetime
import hashlib
import json
from pathlib import Path, PurePosixPath, PureWindowsPath
import re
import shutil
import struct
import tempfile
import zipfile
import xml.etree.ElementTree as ET

REPO = Path(__file__).resolve().parents[2]
RELEASE_VERSION = ET.parse(REPO/'Directory.Build.props').findtext('.//Version')
VERSION = RELEASE_VERSION.split('-')[0]
ALPHA_ACCEPTANCE = ("windows_companion", "offline_archive_export", "toolbar_install_load", "simconnect_live",
                    "live_mission_smoke", "runtime_dependencies", "release_contents", "installer_install",
                    "setup_wizard", "upgrade_preservation", "uninstall_preservation")
EXTENDED_ACCEPTANCE = ("live_recording_recovery", "storage_migration", "mach_live", "toolbar_mission_sync", "vr_performance", "ipad_safari")
ACCEPTANCE = ALPHA_ACCEPTANCE + EXTENDED_ACCEPTANCE
RUNTIME_EXPORTS = ("SimConnect_Open", "SimConnect_Close", "SimConnect_CallDispatch", "SimConnect_AddToDataDefinition",
                   "SimConnect_RequestDataOnSimObject", "SimConnect_SubscribeToSystemEvent", "SimConnect_RequestSystemState",
                   "SimConnect_SubscribeToCommBusEvent", "SimConnect_CallCommBusEvent")
CHECK_HELP = {
    "windows_companion": "On real supported Windows: installed AeroScience launches from Start and automatically opens its dashboard, without a terminal/server command.",
    "offline_archive_export": "With MSFS closed: the selected local archive and saved mission details/export are accessible; existing history remains intact.",
    "toolbar_install_load": "With MSFS closed: graphical Settings installs the included genuine SDK package; after starting MSFS its toolbar opens.",
    "simconnect_live": "In MSFS: the loaded aircraft and fresh actual SimConnect telemetry appear; no generated data is substituted.",
    "live_mission_smoke": "Start and end a short actual flight mission, inspect its recorded samples and confirm retained history after restarting AeroScience.",
    "runtime_dependencies": "On real Windows: included licensed native runtime loads with required exports; supported graphical Visual C++ prerequisites work without SDK/manual DLL copying.",
    "release_contents": "Verify the exact candidate installer checksum and installed files against its receipt; inspect that package/runtime are complete and no personal data is included.",
    "installer_install": "Run this exact setup EXE on Windows: branding/path/progress, bundled prerequisites, Start shortcut, optional desktop shortcut, Installed Apps registration and launch-at-finish work. Test a clean Windows account/environment without developer tools.",
    "setup_wizard": "Test graphical first-run storage paths on separate drives, capacity/warning, Community detection/browse/confirmation, MSFS-running guard, toolbar installation and Skip for Now. Settings can revisit both.",
    "upgrade_preservation": "Install over an existing application. Confirm existing settings, both storage paths, mission IDs/modes/samples and library files survive; requested changes use verified migrations and keep originals.",
    "uninstall_preservation": "Use Windows Installed Apps to uninstall. Confirm app/shortcuts are removed, data/configuration remain, and keeping or explicitly removing the toolbar works with unrelated add-ons preserved. Reinstall and confirm archive access."
}


def require(condition, message):
    if not condition:
        raise ValueError(message)


def identity(version=RELEASE_VERSION):
    require(isinstance(version, str) and re.fullmatch(r'(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?', version),
            'Use a semantic release version without a leading v or build metadata.')
    base, _, suffix = version.partition('-')
    root = f'AeroScience-v{version}-win-x64'
    title = f'AeroScience v{base}' + (' — Live Flight Recording Alpha' if suffix.startswith('alpha') else ' — Prerelease' if suffix else '')
    return dict(version=version, package_version=base, tag='v'+version, root_name=root,
                asset_name=root+'.zip', candidate_name=root+'-CANDIDATE.zip', setup_name=f'AeroScience-Setup-v{version}.exe',
                setup_record_name=f'AeroScience-Setup-v{version}.release.json', prerelease=bool(suffix), title=title)


def read_json(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))


def valid_commit(value):
    return isinstance(value, str) and re.fullmatch(r'[a-fA-F0-9]{40}', value) is not None


def validate_date(value, label):
    require(isinstance(value, str) and bool(value.strip()), f'{label} is required.')
    parsed = datetime.fromisoformat(value.replace('Z', '+00:00'))
    require(parsed.utcoffset() is not None, f'{label} must include a timezone, normally Z.')


def files(root):
    require(root.is_dir(), f"Directory does not exist: {root}")
    result = {}
    for path in root.rglob("*"):
        require(not path.is_symlink(), f"Symlinks are not accepted in release inputs: {path}")
        if path.is_file():
            result[path.relative_to(root).as_posix()] = path
    return result


def validate_community(root, package_version=VERSION):
    found = files(root)
    # The Windows SDK can lowercase layout paths independently of the disk names.
    # Keep original names for the SDK receipt; only matching is case-insensitive.
    indexed = {}
    for name, path in found.items():
        key = name.casefold()
        require(key not in indexed, f"Package contains case-colliding paths: {name}")
        indexed[key] = path
    require({"manifest.json", "layout.json"} <= indexed.keys(), "SDK-built manifest.json and layout.json are required.")
    manifest = json.loads(indexed["manifest.json"].read_text(encoding="utf-8-sig"))
    require(manifest.get("package_version") == package_version, "Community package version differs from the release.")
    require(str(manifest.get("content_type", "")).upper() == "MISC", "Expected MISC toolbar content type.")
    require("AeroScience" in manifest.get("title", ""), "Unexpected package title.")
    minimum_game_version = manifest.get("minimum_game_version")
    require(isinstance(minimum_game_version, str)
            and re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+(?:\.[0-9]+)?", minimum_game_version) is not None,
            f"SDK minimum_game_version is missing/invalid: {minimum_game_version!r}; expected three or four numeric components.")
    require(manifest.get("dependencies") == [], "This toolbar must not depend on an aircraft package.")
    layout = json.loads(indexed["layout.json"].read_text(encoding="utf-8-sig"))
    declared = set()
    for item in layout.get("content", []):
        name = item.get("path", "").replace("\\", "/")
        path = PurePosixPath(name)
        require(name and not path.is_absolute() and not PureWindowsPath(name).drive
                and ".." not in path.parts and path.as_posix() == name, "Unsafe/noncanonical layout path.")
        key = name.casefold()
        require(key not in declared, "Duplicate layout entry.")
        require(key in indexed and key != "layout.json", f"Layout references a missing or invalid file: {name}")
        require(type(item.get("size")) is int and item["size"] == indexed[key].stat().st_size, f"Incorrect layout size: {name}")
        require(type(item.get("date")) is int and item["date"] > 0, f"Invalid SDK FILETIME: {name}")
        declared.add(key)
    require(set(indexed) - {"manifest.json", "layout.json"} <= declared, "Package has files absent from layout.json.")
    expected = {"InGamePanels/aeroscience_panel.spb", "html_ui/InGamePanels/AeroScience/AeroScience.html",
                "html_ui/InGamePanels/AeroScience/AeroScience.js", "html_ui/InGamePanels/AeroScience/AeroScience.css",
                "html_ui/icons/toolbar/ICON_AEROSCIENCE.svg"}
    require({name.casefold() for name in expected} <= indexed.keys(), "Compiled panel registration or toolbar assets are missing.")
    binary = indexed["ingamepanels/aeroscience_panel.spb"].read_bytes()
    require(len(binary) > 32 and not binary.lstrip().startswith((b"<", b"{")), "Panel is source/placeholder content, not an SDK-compiled SPB.")
    html = indexed["html_ui/ingamepanels/aeroscience/aeroscience.html"].read_text(encoding="utf-8-sig")
    require('panel-id="PANEL_AEROSCIENCE"' in html and "AeroScience.js" in html and "AeroScience.css" in html, "Panel references are inconsistent.")
    return found


def validate_companion(root, require_simconnect=False):
    found = files(root)
    expected = {"AeroScience.exe", "AeroScience.dll", "AeroScience.deps.json",
                "AeroScience.runtimeconfig.json", "AeroScience.Engine.dll", "coreclr.dll", "hostpolicy.dll",
                "System.Windows.Forms.dll",
                "wwwroot/index.html", "wwwroot/app.js", "wwwroot/app.css"}
    if require_simconnect:
        expected.add("SimConnect.dll")
    require(expected <= found.keys(), "Missing self-contained Windows companion files or official SimConnect runtime.")
    image = found["AeroScience.exe"].read_bytes()
    require(len(image) >= 64 and image[:2] == b"MZ", "Companion is not a published Windows executable.")
    offset = struct.unpack_from("<I", image, 60)[0]
    require(offset + 6 <= len(image) and image[offset:offset+4] == b"PE\0\0" and struct.unpack_from("<H", image, offset+4)[0] == 0x8664,
            "Companion must target Windows x64.")
    require(offset + 94 <= len(image) and struct.unpack_from("<H", image, offset+24)[0] == 0x20b
            and struct.unpack_from("<H", image, offset+92)[0] == 2,
            "Companion must be a graphical Windows application, not a console launcher.")
    forbidden = ("Launch-Simulated.cmd", "Launch-SimConnect.cmd", "AeroScience.Engine.exe", "AeroScience.TestHost.dll", "AeroScience.Tests.dll")
    require(not any(PurePosixPath(name).name.casefold() in {item.casefold() for item in forbidden} for name in found), "Release contains developer/test launchers or test assemblies.")
    require([name for name in found if name.casefold().endswith('.exe')] == ['AeroScience.exe'],
            'Only AeroScience.exe should be included as an executable entry point.')
    require(not any(PurePosixPath(name).suffix.casefold() in {".db", ".sqlite", ".log"}
                    or PurePosixPath(name).name.casefold() in {"settings.json", "missions.db-wal", "missions.db-shm"}
                    for name in found), "Release contains personal mission data, configuration or logs.")
    require(not any(PurePosixPath(name).suffix.casefold() in {'.pdb', '.obj', '.lib', '.cs', '.cpp', '.py', '.ps1', '.cmd', '.env'}
                    or any(part.casefold() in {'.git', '.github', 'node_modules', 'src', 'obj', 'bin'} for part in PurePosixPath(name).parts)
                    for name in found), 'Release contains source, debugging/build files, credentials or development tools.')
    require(not any(PurePosixPath(name).suffix.casefold() in {'.eml', '.msg', '.mbox', '.pst', '.ost'} for name in found),
            'Email/correspondence archives must remain private and outside distribution inputs.')
    return found


def validate_receipt(path, community_files):
    receipt = json.loads(path.read_text(encoding="utf-8-sig"))
    require(receipt.get("variant") == "toolbar-full", "A full-toolbar SDK build receipt is required; diagnostic variants cannot ship as the full toolbar.")
    require(receipt.get("built_utc") and receipt.get("builder_sha256"), "SDK build provenance is incomplete.")
    require(receipt.get("sdk_contract") == "passed" and bool(receipt.get("sdk_header_sha256")), "Installed SDK ABI contract evidence is missing.")
    expected = {name: hashlib.sha256(path.read_bytes()).hexdigest() for name, path in community_files.items()}
    require(receipt.get("files") == expected, "Community files differ from the recorded SDK build.")


def validate_acceptance(path, version=RELEASE_VERSION, source_commit=None, payload_sha256=None):
    acceptance = read_json(path)
    require(acceptance.get("version") == version and acceptance.get("tester") and acceptance.get("tested_utc"), "Release acceptance identity/date/version missing.")
    validate_date(acceptance['tested_utc'], 'Acceptance test date')
    require(valid_commit(acceptance.get('source_commit')), 'Acceptance must identify the exact tested source commit.')
    if source_commit is not None:
        require(acceptance['source_commit'] == source_commit, 'Acceptance identifies a different source commit.')
    if payload_sha256 is not None:
        require(acceptance.get('payload_sha256') == payload_sha256, 'Acceptance does not match the validated candidate payload; test this exact build.')
    required = ALPHA_ACCEPTANCE if identity(version)['prerelease'] else ACCEPTANCE
    for name in required:
        check = acceptance.get("checks", {}).get(name, {})
        require(check.get("result") == "passed" and bool(check.get("evidence", "").strip()), f"Manual release acceptance is pending: {name}")
    return acceptance


def validate_runtime(root, report_path, approval_path):
    runtime = root/'SimConnect.dll'
    require(runtime.is_file(), 'The full distribution needs the validated official native SimConnect runtime; a personal SDK installation alone does not grant redistribution.')
    digest = hashlib.sha256(runtime.read_bytes()).hexdigest()
    report = read_json(report_path)
    require(report.get('architecture') == 'Windows x64' and report.get('export_check') == 'passed'
            and set(RUNTIME_EXPORTS) <= set(report.get('exports', [])) and str(report.get('sha256', '')).lower() == digest,
            'A real Windows loading/export report matching the bundled SimConnect.dll is required; MetadataOnly is insufficient.')
    approval = read_json(approval_path)
    require(approval.get('format_version') == 2 and approval.get('redistribution_permitted') is True and str(approval.get('runtime_sha256', '')).lower() == digest
            and approval.get('permission_for_file') == 'SimConnect.dll' and approval.get('reviewed_by')
            and isinstance(approval.get('permission_summary'), str) and approval['permission_summary'].strip()
            and approval.get('conditions_reviewed') is True and isinstance(approval.get('conditions_summary'), str)
            and approval['conditions_summary'].strip() and approval.get('supporting_evidence_private') is True,
            'A version 2 maintainer permission attestation matching this DLL and its reviewed distribution conditions is required. Use New-RuntimeApproval.ps1; keep original correspondence private.')
    validate_date(approval.get('reviewed_utc'), 'Runtime permission review date')
    return report, public_runtime_approval(approval)


def public_runtime_approval(approval):
    # Only explicitly public summaries belong in the ZIP. Never quote private evidence.
    public = {key: approval[key] for key in ('format_version', 'redistribution_permitted', 'permission_for_file',
        'runtime_sha256', 'permission_summary', 'conditions_reviewed', 'conditions_summary', 'supporting_evidence_private',
        'reviewed_by', 'reviewed_utc')}
    public['runtime_sha256'] = public['runtime_sha256'].lower()
    public['permission_source'] = 'Project maintainer confirmation; supporting authorization retained privately.'
    return public


def validate_provenance(root, receipt_path, version=RELEASE_VERSION):
    metadata = read_json(root/'build-info.json')
    receipt = read_json(receipt_path)
    require(metadata.get('version') == version and metadata.get('target') == 'win-x64' and metadata.get('source_dirty') is False,
            'Release companion must be built from clean, committed source for this version.')
    require(valid_commit(metadata.get('source_commit')) and receipt.get('source_commit') == metadata['source_commit']
            and receipt.get('source_dirty') is False, 'App and SDK toolbar must identify the same clean committed source revision.')
    return metadata['source_commit']


def payload_digest(root):
    # These two review documents change after testing; every operational byte remains identical.
    ignored = {'ACCEPTANCE.json', 'RELEASE-STATUS.txt'}
    entries = [(name, hashlib.sha256(path.read_bytes()).hexdigest()) for name, path in sorted(files(root).items()) if name not in ignored]
    return hashlib.sha256(json.dumps(entries, separators=(',', ':')).encode()).hexdigest()


def validate_payload(root, final=False, version=RELEASE_VERSION):
    validate_companion(root, require_simconnect=True)
    community = validate_community(root/'Toolbar'/'aeroscience-toolbar', identity(version)['package_version'])
    receipt = root/'Toolbar'/'sdk-build.json'
    validate_receipt(receipt, community)
    commit = validate_provenance(root, receipt, version)
    approval_path = root/'Dependencies'/'runtime-approval.json'
    _, public_approval = validate_runtime(root, root/'Dependencies'/'runtime-check.json', approval_path)
    require(read_json(approval_path) == public_approval,
            'The distributable approval record contains non-public or unexpected fields. Keep correspondence/excerpts/private references outside the package.')
    digest = payload_digest(root)
    if final:
        require(not (root/'RELEASE-STATUS.txt').exists(), 'A candidate status marker cannot ship as the final release.')
        validate_acceptance(root/'ACCEPTANCE.json', version, commit, digest)
    return commit, digest


def acceptance_template(commit, digest):
    return dict(version=RELEASE_VERSION, source_commit=commit, payload_sha256=digest, tester='', tested_utc='',
                checks={name: dict(result='pending', evidence='', instructions=CHECK_HELP.get(name, 'Extended acceptance; may remain pending for this prerelease.')) for name in ACCEPTANCE})


def prepare_candidate(companion, community, receipt_path, runtime_report, runtime_approval, destination):
    require(not destination.exists(), f'Refusing to overwrite an existing candidate: {destination}. Keep tested candidates separate.')
    validate_companion(companion, require_simconnect=True)
    found = validate_community(community)
    validate_receipt(receipt_path, found)
    validate_provenance(companion, receipt_path)
    report, approval = validate_runtime(companion, runtime_report, runtime_approval)
    try:
        shutil.copytree(companion, destination)
        shutil.copytree(community, destination/'Toolbar'/'aeroscience-toolbar')
        shutil.copy2(receipt_path, destination/'Toolbar'/'sdk-build.json')
        (destination/'Dependencies').mkdir()
        # Do not publish a tester's absolute SDK path or environment details.
        report = {key: report[key] for key in ('checked_utc', 'sha256', 'file_version', 'architecture', 'export_check', 'exports') if key in report}
        (destination/'Dependencies'/'runtime-check.json').write_text(json.dumps(report, indent=2)+'\n')
        (destination/'Dependencies'/'runtime-approval.json').write_text(json.dumps(approval, indent=2)+'\n')
        for name in ('LICENSE', 'THIRD-PARTY-NOTICES.md', 'CHANGELOG.md', 'INSTALL.md'):
            shutil.copy2(REPO/name, destination/name)
        (destination/'docs').mkdir()
        for name in ('alpha-testing.md', 'runtime-distribution.md'):
            shutil.copy2(REPO/'docs'/name, destination/'docs'/name)
        shutil.copy2(REPO/'docs'/'release-readme.md', destination/'README.md')
        (destination/'DEFAULTS.json').write_text(json.dumps(dict(description='Built-in defaults; use graphical Settings to change them.',
            mission_directory=r'%LOCALAPPDATA%\AeroScience', science_library_directory=r'%LOCALAPPDATA%\AeroScience\ScienceLibrary',
            sampling_interval_ms=1000, enable_lan=False, mission_mode='universal', telemetry='SimConnect'), indent=2)+'\n')
        commit, digest = validate_payload(destination)
        (destination/'ACCEPTANCE.json').write_text(json.dumps(acceptance_template(commit, digest), indent=2)+'\n')
        (destination/'RELEASE-STATUS.txt').write_text('INTERMEDIATE PAYLOAD: real installer/Windows/MSFS acceptance is pending. Build the setup EXE, test that exact installer, then finalize without rebuilding.\n')
    except Exception:
        # Retain partial files for diagnosis; no final archive is produced.
        raise
    return commit, digest


def write_checksum(output):
    digest = hashlib.sha256(output.read_bytes()).hexdigest()
    output.with_name(output.name+'.sha256').write_text(digest+'  '+output.name+'\n')
    return digest


def finalize_candidate(candidate, acceptance_path, output):
    commit, digest = validate_payload(candidate)
    validate_acceptance(acceptance_path, RELEASE_VERSION, commit, digest)
    require(output.name == identity()['asset_name'], 'Use the required versioned Windows x64 release filename.')
    output.parent.mkdir(parents=True, exist_ok=True)
    require(not output.exists() and not output.with_suffix('.zip.sha256').exists(), 'Refusing to overwrite an existing release/checksum.')
    with tempfile.TemporaryDirectory(prefix='aeroscience-final-', dir=output.parent) as work:
        root = Path(work)/identity()['root_name']
        shutil.copytree(candidate, root)
        (root/'RELEASE-STATUS.txt').unlink(missing_ok=True)
        shutil.copyfile(acceptance_path, root/'ACCEPTANCE.json')
        validate_payload(root, final=True)
        staged = Path(work)/output.name
        zip_tree(root, staged)
        write_checksum(staged)
        verify_archive(staged, staged.with_suffix('.zip.sha256'))
        staged.rename(output)
        staged.with_suffix('.zip.sha256').rename(output.with_suffix('.zip.sha256'))


def verify_archive(archive, checksum):
    meta = identity()
    require(archive.name == meta['asset_name'], 'Unexpected release ZIP name.')
    actual = hashlib.sha256(archive.read_bytes()).hexdigest()
    require(checksum.read_text().strip() == actual+'  '+archive.name, 'ZIP checksum does not match its SHA-256 sidecar.')
    with tempfile.TemporaryDirectory(prefix='aeroscience-inspect-') as work:
        with zipfile.ZipFile(archive) as zip:
            require(zip.testzip() is None, 'Release ZIP CRC verification failed.')
            seen = set()
            for info in zip.infolist():
                path = PurePosixPath(info.filename)
                require(not path.is_absolute() and '..' not in path.parts and '\\' not in info.filename
                        and not PureWindowsPath(info.filename).drive and path.parts and path.parts[0] == meta['root_name']
                        and info.filename.casefold() not in seen and (info.external_attr >> 16) & 0o170000 != 0o120000,
                        'Unsafe, duplicate or unexpected release ZIP path.')
                seen.add(info.filename.casefold())
            zip.extractall(work)
        root = Path(work)/meta['root_name']
        commit, digest = validate_payload(root, final=True)
        acceptance = read_json(root/'ACCEPTANCE.json')
    return dict(**meta, source_commit=commit, payload_sha256=digest, sha256=actual, acceptance=acceptance)


def release_notes(info):
    optional = info['acceptance'].get('checks', {})
    limits = [name.replace('_', ' ') for name in EXTENDED_ACCEPTANCE if optional.get(name, {}).get('result') != 'passed']
    installer = info.get('distribution') == 'installer'
    instructions = ('Run the setup EXE, follow installation, then the guided storage and MSFS toolbar setup. Launch AeroScience from Start; its dashboard opens automatically. Updates preserve existing storage settings. Windows Installed Apps provides an uninstaller that retains mission/science data and asks before removing the toolbar.' if installer else 'Optional portable download: extract all files and double-click AeroScience.exe. The Windows setup EXE is the recommended installation method.')
    return f'''# {info['title']}

AeroScience is an MSFS 2024 airborne science simulation and flight research recording application.

**Download [{info['asset_name']}](https://github.com/JustSeanC/AeroScience/releases/download/{info['tag']}/{info['asset_name']})** under Assets. {instructions} Then start MSFS, load a flight and open AeroScience from its toolbar. No developer tools, PowerShell, separate toolbar download or manual DLL copying is required. The installer bundles the self-contained app, authorized native SimConnect runtime, genuine SDK-compiled toolbar and Microsoft Visual C++ prerequisite. A prerequisite installation may request Windows administrator approval. No internet connection is needed by the installed mission archive.

Implemented features include live aircraft telemetry, simulator-provided Mach, Universal/Realistic recording modes, Mission History, offline archive access, JSON mission exports, independent mission-database/science-library storage, a browser dashboard and an MSFS toolbar. Minimum Windows startup, local archive, graphical toolbar installation and short live-recording acceptance were recorded for the exact included payload. Feature availability does not imply every aircraft/device or edge case was tested.

This is {'an early prerelease' if info['prerelease'] else 'a release'}. Extended checks still awaiting results: {', '.join(limits) if limits else 'none in the recorded checklist'}. See ACCEPTANCE.json for actual evidence. Advanced instruments, mapping and research mission planning are planned for later versions. The library currently provides storage structure only. Existing history remains local and is not deleted by an upgrade.

Use **Collect Feedback** in AeroScience, add reproduction steps and save the ZIP. Review it for private information, then report the problem at https://github.com/JustSeanC/AeroScience/issues. Do not attach your mission database or personal observations unless you choose to share them.

Source: https://github.com/JustSeanC/AeroScience/commit/{info['source_commit']}

SHA-256: `{info['sha256']}`
'''


def zip_tree(source, output):
    output.parent.mkdir(parents=True, exist_ok=True)
    require(not output.exists(), f"Refusing to overwrite existing release: {output}")
    # A failed ZIP operation cannot leave a seemingly complete final archive.
    temporary = output.with_suffix(".zip.partial")
    try:
        with zipfile.ZipFile(temporary, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
            for path in sorted(source.rglob("*")):
                if path.is_file():
                    archive.write(path, path.relative_to(source.parent).as_posix())
        temporary.replace(output)
    finally:
        temporary.unlink(missing_ok=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)
    metadata = sub.add_parser('metadata'); metadata.add_argument('--github-output', type=Path)
    validate = sub.add_parser("validate"); validate.add_argument("--community", type=Path, required=True)
    preview = sub.add_parser("companion-preview"); preview.add_argument("--companion", type=Path, required=True); preview.add_argument("--output", type=Path, required=True)
    prepare = sub.add_parser('prepare')
    for key in ('companion', 'community', 'sdk-receipt', 'runtime-report', 'runtime-approval', 'destination', 'output'):
        prepare.add_argument('--'+key, type=Path, required=True)
    prepare.add_argument('--runtime-redistribution-confirmed', action='store_true')
    release = sub.add_parser("release")
    for key in ('candidate', 'acceptance', 'output'):
        release.add_argument('--'+key, type=Path, required=True)
    verify = sub.add_parser('verify-release')
    verify.add_argument('--archive', type=Path, required=True); verify.add_argument('--checksum', type=Path, required=True)
    verify.add_argument('--notes-output', type=Path)
    verify.add_argument('--expected-commit')
    args = parser.parse_args()
    meta = identity()
    if args.command == 'metadata':
        if args.github_output:
            with args.github_output.open('a') as file:
                for key, value in meta.items(): file.write(f'{key}={str(value).lower() if isinstance(value, bool) else value}\n')
        print(json.dumps(meta)); return
    if args.command == "validate":
        found = validate_community(args.community); print(f"Community structural validation passed: {len(found)} files. Simulator loading remains a separate check."); return
    if args.command == 'verify-release':
        info = verify_archive(args.archive, args.checksum)
        if args.expected_commit:
            require(info['source_commit'] == args.expected_commit, 'Release payload differs from the selected source revision.')
        if args.notes_output: args.notes_output.write_text(release_notes(info), encoding='utf-8')
        print(json.dumps(info)); return
    if args.command == 'prepare':
        require(args.runtime_redistribution_confirmed, 'Confirm specific official runtime redistribution permission before preparing a candidate.')
        require(args.output.name == meta['candidate_name'], 'Candidates need the explicit CANDIDATE filename; do not publish an untested build.')
        require(args.destination.name == meta['root_name'], 'Use the versioned candidate folder name.')
        require(not args.output.exists() and not args.output.with_suffix('.zip.sha256').exists(), 'Refusing to overwrite an existing candidate ZIP/checksum.')
        prepare_candidate(args.companion, args.community, args.sdk_receipt, args.runtime_report, args.runtime_approval, args.destination)
        zip_tree(args.destination, args.output.resolve()); digest = write_checksum(args.output)
        with zipfile.ZipFile(args.output) as archive: require(archive.testzip() is None, 'Candidate ZIP extraction/CRC verification failed.')
        print(f'Validated intermediate payload {args.output}. SHA-256 {digest}. Build-Installer.ps1 creates the primary setup EXE and installer-specific acceptance record.'); return
    if args.command == 'release':
        finalize_candidate(args.candidate, args.acceptance, args.output.resolve())
        print(f'Final release ZIP: {args.output}\nSHA-256: {hashlib.sha256(args.output.read_bytes()).hexdigest()}\nVerified exact tested payload. Nothing has been published.'); return
    companion_files = validate_companion(args.companion)
    sdk_runtime_names = {"simconnect.dll", "microsoft.flightsimulator.simconnect.dll"}
    require(not any(PurePosixPath(name).name.casefold() in sdk_runtime_names for name in companion_files),
            "Companion previews exclude the proprietary SimConnect runtime; keep personal live-test files in a separate folder.")
    require("Preview" in args.output.name, "Companion-only development archives must be explicitly labeled Preview.")
    with tempfile.TemporaryDirectory(prefix="aeroscience-package-") as work:
        root = Path(work)/f"AeroScience-Companion-Preview-v{RELEASE_VERSION}"; root.mkdir()
        shutil.copytree(args.companion, root, dirs_exist_ok=True)
        for doc in ("README.md", "INSTALL.md", "CHANGELOG.md", "LICENSE", "THIRD-PARTY-NOTICES.md"):
            shutil.copy2(REPO/doc, root/doc)
        shutil.copytree(REPO/"docs", root/"docs")
        (root/"PREVIEW.txt").write_text("Windows graphical app developer preview. Open AeroScience.exe; the local archive is available without MSFS. No Community toolbar or proprietary SimConnect runtime is bundled. New Windows UI, migration, Mach and hardware checks remain pending. This is not an installation-ready alpha.\n")
        zip_tree(root, args.output.resolve())
    write_checksum(args.output)
    print(f"Created {args.output} ({args.output.stat().st_size:,} bytes); SHA-256 sidecar written.")


if __name__ == "__main__":
    try:
        main()
    except (ValueError, OSError, KeyError, json.JSONDecodeError, zipfile.BadZipFile) as error:
        raise SystemExit(f"Packaging refused: {error}")
