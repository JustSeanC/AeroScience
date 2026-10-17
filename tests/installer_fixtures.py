"""Synthetic installer/SDK bytes for control-flow tests only; never release inputs."""
import json
from pathlib import Path
import struct
import sys

sys.path.insert(0, str(Path(__file__).resolve().parents[1]/'scripts/release'))
import installer_release as installer


def executable_fixture():
    image = bytearray(2048); image[:2] = b'MZ'; struct.pack_into('<I', image, 60, 128)
    image[128:132] = b'PE\0\0'
    image[256:310] = b'NOT AN INSTALLER: STRUCTURAL UNIT TEST FIXTURE ONLY'
    return bytes(image)


def candidate(flow, vc_signature='Valid'):
    flow.prepare()
    folder = flow.root/'installer-candidate'
    installer.stage(flow.candidate, folder/'payload', 'a'*40)
    (folder/installer.p.identity()['setup_name']).write_bytes(executable_fixture())
    compiler = flow.root/'ISCC.exe'; compiler.write_bytes(b'NOT A COMPILER: unit fixture')
    vc = flow.root/'VC_redist.x64.exe'; vc.write_bytes(executable_fixture())
    report = flow.root/'tool-report.json'
    report.write_text(json.dumps(dict(compiler_sha256=installer.sha(compiler), compiler_version='UNIT FIXTURE ONLY',
        vc_sha256=installer.sha(vc), vc_version='UNIT FIXTURE ONLY', vc_signature=vc_signature, vc_architecture='x64',
        vc_publisher='Microsoft Corporation - SYNTHETIC UNIT FIXTURE, NOT SIGNATURE EVIDENCE')))
    installer.record(folder, compiler, vc, report)
    acceptance = installer.p.read_json(folder/'ACCEPTANCE.json')
    acceptance.update(tester='UNIT FIXTURE ONLY', tested_utc='2026-10-17T00:00:00Z')
    for name in installer.p.ALPHA_ACCEPTANCE:
        acceptance['checks'][name].update(result='passed', evidence='STRUCTURAL FIXTURE ONLY; NOT WINDOWS ACCEPTANCE')
    installer.write_json(flow.acceptance, acceptance)
    return folder
