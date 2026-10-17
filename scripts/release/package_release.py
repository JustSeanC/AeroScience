#!/usr/bin/env python3
"""Fail-closed packaging of real SDK outputs; never manufacture manifests or compiled panels."""
from __future__ import annotations
import argparse
import hashlib
import json
from pathlib import Path, PurePosixPath, PureWindowsPath
import re
import shutil
import struct
import tempfile
import zipfile

VERSION = "0.1.1"
RELEASE_VERSION = "0.1.1-alpha"
REPO = Path(__file__).resolve().parents[2]
ACCEPTANCE = ("windows_companion", "live_recording_recovery", "offline_archive_export", "storage_migration", "mach_live", "simconnect_live", "toolbar_install_load",
              "toolbar_mission_sync", "vr_performance", "ipad_safari", "release_contents")


def require(condition, message):
    if not condition:
        raise ValueError(message)


def files(root):
    require(root.is_dir(), f"Directory does not exist: {root}")
    result = {}
    for path in root.rglob("*"):
        require(not path.is_symlink(), f"Symlinks are not accepted in release inputs: {path}")
        if path.is_file():
            result[path.relative_to(root).as_posix()] = path
    return result


def validate_community(root):
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
    require(manifest.get("package_version") == VERSION, "Community package version differs from the release.")
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
    require(not any(PurePosixPath(name).suffix.casefold() in {".db", ".sqlite", ".log"}
                    or PurePosixPath(name).name.casefold() in {"settings.json", "missions.db-wal", "missions.db-shm"}
                    for name in found), "Release contains personal mission data, configuration or logs.")
    return found


def validate_receipt(path, community_files):
    receipt = json.loads(path.read_text(encoding="utf-8-sig"))
    require(receipt.get("variant") == "toolbar-full", "A full-toolbar SDK build receipt is required; diagnostic variants cannot ship as the full toolbar.")
    require(receipt.get("built_utc") and receipt.get("builder_sha256"), "SDK build provenance is incomplete.")
    require(receipt.get("sdk_contract") == "passed" and bool(receipt.get("sdk_header_sha256")), "Installed SDK ABI contract evidence is missing.")
    expected = {name: hashlib.sha256(path.read_bytes()).hexdigest() for name, path in community_files.items()}
    require(receipt.get("files") == expected, "Community files differ from the recorded SDK build.")


def validate_acceptance(path):
    acceptance = json.loads(path.read_text(encoding="utf-8"))
    require(acceptance.get("version") == RELEASE_VERSION and acceptance.get("tester") and acceptance.get("tested_utc"), "Release acceptance identity/date/version missing.")
    for name in ACCEPTANCE:
        check = acceptance.get("checks", {}).get(name, {})
        require(check.get("result") == "passed" and bool(check.get("evidence", "").strip()), f"Manual release acceptance is pending: {name}")


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
    validate = sub.add_parser("validate"); validate.add_argument("--community", type=Path, required=True)
    preview = sub.add_parser("companion-preview"); preview.add_argument("--companion", type=Path, required=True); preview.add_argument("--output", type=Path, required=True)
    release = sub.add_parser("release")
    for key in ("community", "companion", "sdk-receipt", "acceptance", "output"):
        release.add_argument("--" + key, type=Path, required=True)
    release.add_argument("--runtime-redistribution-confirmed", action="store_true")
    args = parser.parse_args()
    if args.command == "validate":
        found = validate_community(args.community); print(f"Community structural validation passed: {len(found)} files. Simulator loading remains a separate check."); return
    companion_files = validate_companion(args.companion, require_simconnect=args.command == "release")
    if args.command == "release":
        require(args.runtime_redistribution_confirmed, "Confirm Microsoft's runtime redistribution terms before including the official DLL.")
        found = validate_community(args.community); validate_receipt(args.sdk_receipt, found); validate_acceptance(args.acceptance)
        metadata = json.loads((args.companion/'build-info.json').read_text(encoding='utf-8-sig'))
        receipt = json.loads(args.sdk_receipt.read_text(encoding='utf-8-sig'))
        require(metadata.get('version') == RELEASE_VERSION and metadata.get('source_dirty') is False,
                'Release companion must be built from clean, committed alpha source.')
        require(isinstance(metadata.get('source_commit'), str) and re.fullmatch(r'[a-fA-F0-9]{40}', metadata['source_commit'])
                and receipt.get('source_commit') == metadata['source_commit'],
                'App and SDK toolbar must identify the same committed source revision.')
        require(args.output.name == f"AeroScience-v{RELEASE_VERSION}.zip", "Use the required versioned release filename.")
        name = f"AeroScience-v{RELEASE_VERSION}"
    else:
        sdk_runtime_names = {"simconnect.dll", "microsoft.flightsimulator.simconnect.dll"}
        require(not any(PurePosixPath(name).name.casefold() in sdk_runtime_names for name in companion_files),
                "Companion previews exclude the proprietary SimConnect runtime; keep personal live-test files in a separate folder.")
        require("Preview" in args.output.name, "Companion-only development archives must be explicitly labeled Preview.")
        name = f"AeroScience-Companion-Preview-v{RELEASE_VERSION}"
    with tempfile.TemporaryDirectory(prefix="aeroscience-package-") as work:
        root = Path(work)/name; root.mkdir()
        shutil.copytree(args.companion, root, dirs_exist_ok=True)
        for doc in ("README.md", "INSTALL.md", "CHANGELOG.md", "LICENSE", "THIRD-PARTY-NOTICES.md"):
            shutil.copy2(REPO/doc, root/doc)
        shutil.copytree(REPO/"docs", root/"docs")
        companion_root = root
        if args.command == "release":
            shutil.copytree(args.community, companion_root/"Toolbar"/"aeroscience-toolbar")
            shutil.copy2(args.sdk_receipt, companion_root/"Toolbar"/"sdk-build.json"); shutil.copy2(args.acceptance, root/"ACCEPTANCE.json")
        else:
            (root/"PREVIEW.txt").write_text("Windows graphical app developer preview. Open AeroScience.exe; the local archive is available without MSFS. No Community toolbar or proprietary SimConnect runtime is bundled. New Windows UI, migration, Mach and hardware checks remain pending. This is not an installation-ready alpha.\n")
        zip_tree(root, args.output.resolve())
    digest = hashlib.sha256(args.output.read_bytes()).hexdigest()
    args.output.with_suffix(".zip.sha256").write_text(digest + "  " + args.output.name + "\n")
    print(f"Created {args.output} ({args.output.stat().st_size:,} bytes); SHA-256 sidecar written.")


if __name__ == "__main__":
    try:
        main()
    except (ValueError, OSError, KeyError, json.JSONDecodeError) as error:
        raise SystemExit(f"Packaging refused: {error}")
