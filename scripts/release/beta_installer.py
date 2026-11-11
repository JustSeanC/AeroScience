"""Private beta installer integrity. Does not finalize or publish production releases."""
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import installer_release as installer
import package_release as p
import private_validation as private
from private_beta_identity import identity, BETA_VERSION, ENGINEERING_SOURCE

VERSION = '0.7.0-beta.1'
SETUP = f'AeroScience-Setup-v{VERSION}.exe'
RECEIPT = 'BETA-INSTALLER-RECEIPT.json'
SOURCE_RECEIPT = 'SOURCE-VALIDATION-RECEIPT.json'
# The production publisher already removes these root-level build/runtime tools.
# Never generalize this to test assemblies, arbitrary executables or user files.
BUILD_EXECUTABLES = frozenset({'aeroscience.engine.exe', 'createdump.exe'})


def write(path, value):
    installer.write_json(path, value)


def verify_application(folder, expected, receipt=None, version=VERSION):
    profile=identity(version);beta_version=version if version==BETA_VERSION else None
    p.require(p.valid_commit(expected), 'An exact payload source commit is required.')
    if receipt is None:
        private.verify(folder,beta_version)
        receipt = p.read_json(folder/'VALIDATION-RECEIPT.json')
    info = p.read_json(folder/'build-info.json')
    p.require(info.get('version') == profile['application_version'] and info.get('profile') == 'validation'
              and info.get('target') == 'win-x64' and info.get('source_dirty') is False
              and info.get('source_commit') == expected and receipt.get('source_commit') == expected
              and receipt.get('profile') == 'validation'
              and receipt.get('public_release_approved') is False,
              'Private beta requires an exact-source isolated v'+profile['application_version']+' Windows validation payload.')
    if beta_version:private.validate_beta_profile(folder,expected,beta_version)
    p.require(not (folder/'ChartRuntime').exists(), 'Optional private chart runtimes are outside this tester installer.')
    for name in ('LICENSE', 'THIRD-PARTY-NOTICES.md'):
        p.require((folder/name).is_file() and installer.sha(folder/name) == installer.sha(p.REPO/name), 'Required licence/notice differs: '+name)
    toolbar = folder/'Toolbar'/profile['toolbar_package']
    p.validate_community(toolbar, validation=True, private_beta_version=beta_version)
    sdk = p.read_json(folder/'Toolbar/sdk-build.json')
    p.require(sdk.get('variant') == 'toolbar-validation' and sdk.get('source_commit') == expected
              and sdk.get('source_dirty') is False and sdk.get('sdk_contract') == 'passed'
              and sdk.get('sdk_header_sha256') and sdk.get('builder_sha256') and sdk.get('built_utc')
              and sdk.get('files') == private.hashes(toolbar),
              'Genuine matching SDK build evidence is required for the beta toolbar.')
    if beta_version:p.require(sdk.get('private_beta_version')==beta_version,'Private beta SDK identity differs.')
    private.validate_native_javascript(sdk.get('native_javascript'), sdk['files'])
    approval = p.read_json(folder/'RUNTIME-AUTHORIZATION.json')
    p.require(approval == p.public_runtime_approval(approval), 'Private runtime authorization fields cannot be distributed.')
    return info


def omitted_files(receipt):
    files = receipt.get('files')
    p.require(isinstance(files, dict) and all(isinstance(name, str) for name in files), 'Source validation inventory is invalid.')
    return {name: digest for name, digest in files.items() if name.casefold() in BUILD_EXECUTABLES}


def verify_deployment(folder, expected, receipt, omitted, version=VERSION):
    p.require(omitted == omitted_files(receipt), 'Only the declared root build executables may be omitted.')
    projected = {name: digest for name, digest in receipt['files'].items() if name not in omitted}
    p.require(private.hashes(folder) == projected, 'Beta application differs from the verified source file projection.')
    verify_application(folder, expected, receipt, version)
    # Remains strict: test assemblies, unknown EXEs, source and personal data fail.
    p.validate_windows_payload(folder, require_simconnect=True)


def stage(folder, destination, expected, runtime_report, version=VERSION):
    profile=identity(version)
    p.require(not (folder.resolve().is_relative_to(destination.resolve()) or destination.resolve().is_relative_to(folder.resolve())), 'Source validation and beta output must not overlap.')
    verify_application(folder, expected, version=version)
    report, _ = p.validate_runtime(folder, runtime_report, folder/'RUNTIME-AUTHORIZATION.json')
    p.require(not destination.exists(), 'Beta payload already exists; preserve it and use a new output folder.')
    original_receipt = folder/'VALIDATION-RECEIPT.json'
    receipt = p.read_json(original_receipt)
    omitted = omitted_files(receipt)
    # Preserve the source receipt outside the deployment subset; never re-stamp it
    # as if removed files were still installed. All copied bytes must match it.
    def ignore(directory, names):
        return [name for name in names if name == 'VALIDATION-RECEIPT.json' or name in omitted] if Path(directory) == folder else []
    shutil.copytree(folder, destination/'application', ignore=ignore)
    shutil.copyfile(original_receipt, destination/SOURCE_RECEIPT)
    verify_deployment(destination/'application', expected, receipt, omitted, version)
    public_report = {key: report[key] for key in ('architecture', 'sha256', 'export_check', 'exports', 'file_version', 'checked_utc') if key in report}
    write(destination/'RUNTIME-CHECK.json', public_report)
    write(destination/'BETA-BUILD.json', dict(format_version=2, version=version, source_commit=expected, profile='validation',
        distribution='private-tester-beta', installed_acceptance='Not verified', public_release_approved=False,
        scientific_release_approved=False, source_validation_receipt_sha256=installer.sha(destination/SOURCE_RECEIPT),
        omitted_build_files=omitted, installer_app_id=profile['app_id'],
        accepted_engineering_source=ENGINEERING_SOURCE if version==BETA_VERSION else None))
    shutil.copyfile(p.REPO/profile['guide'], destination/'START-HERE.md')
    for name in omitted:
        print('Omitted build executable from installer copy: '+name)
    return expected


def payload(folder, expected, version=VERSION):
    profile=identity(version)
    meta = p.read_json(folder/'BETA-BUILD.json')
    p.require(meta.get('format_version') == 2 and meta.get('version') == version and meta.get('distribution') == 'private-tester-beta'
              and meta.get('public_release_approved') is False and meta.get('scientific_release_approved') is False
              and meta.get('source_commit') == expected, 'Private beta identity differs.')
    if version==BETA_VERSION:
        p.require(meta.get('installed_acceptance')=='Not verified' and meta.get('installer_app_id')==profile['app_id'],'Private beta approval/installer identity differs.')
        p.require(meta.get('accepted_engineering_source')==ENGINEERING_SOURCE,'Accepted maintenance source differs.')
    source_receipt = folder/SOURCE_RECEIPT
    p.require(meta.get('source_validation_receipt_sha256') == installer.sha(source_receipt), 'Original validation receipt changed.')
    verify_deployment(folder/'application', expected, p.read_json(source_receipt), meta.get('omitted_build_files'), version)
    p.validate_runtime(folder/'application', folder/'RUNTIME-CHECK.json', folder/'application/RUNTIME-AUTHORIZATION.json')


def recipe_sha():
    return hashlib.sha256((p.REPO/'scripts/installer/AeroScienceBeta.iss').read_text(encoding='utf-8-sig').encode()).hexdigest()


def record(folder, expected, compiler, prerequisite, tool_report, version=VERSION):
    profile=identity(version);setup_name=f'AeroScience-Setup-v{version}.exe'
    p.require(not (folder/RECEIPT).exists(), 'Beta installer receipt already exists.')
    payload(folder/'payload', expected, version)
    setup = folder/setup_name
    installer.pe(setup)
    installer.pe(prerequisite)
    report = p.read_json(tool_report)
    p.require(report.get('vc_signature') == 'Valid' and 'Microsoft Corporation' in report.get('vc_publisher', '')
              and report.get('vc_sha256') == installer.sha(prerequisite) and report.get('vc_architecture') == 'x64'
              and report.get('vc_version'), 'A matching signed Microsoft x64 VC runtime is required.')
    p.require(report.get('compiler_sha256') == installer.sha(compiler)
              and report.get('compiler_version'), 'Matching Inno compiler evidence is required.')
    if version==BETA_VERSION:
        p.require(report.get('setup_product_version')==version and report.get('setup_product_name')==profile['title']
                  and report.get('setup_numeric_version')=='0.7.1.2' and report.get('setup_sha256')==installer.sha(setup),
                  'Compiled beta installer version/source evidence differs.')
    public_tools = {key: report[key] for key in ('compiler_sha256', 'compiler_version', 'vc_sha256', 'vc_version', 'vc_signature', 'vc_publisher', 'vc_architecture')}
    setup_metadata={key:report[key] for key in ('setup_product_version','setup_product_name','setup_numeric_version')} if version==BETA_VERSION else None
    write(folder/RECEIPT, dict(version=version, source_commit=expected, setup_name=setup_name,
        setup_sha256=installer.sha(setup), files=private.hashes(folder/'payload'), compiler=public_tools,
        recipe_sha256=recipe_sha(), installer_app_id=profile['app_id'], scientific_release_approved=False,
        public_release_approved=False, installed_acceptance='Not verified', setup_metadata=setup_metadata))
    p.write_checksum(setup)
    verify(folder, expected, version)


def verify(folder, expected, version=VERSION):
    profile=identity(version);setup_name=f'AeroScience-Setup-v{version}.exe'
    receipt = p.read_json(folder/RECEIPT)
    if version==BETA_VERSION:
        p.require(receipt.get('setup_metadata')==dict(setup_product_version=version,setup_product_name=profile['title'],setup_numeric_version='0.7.1.2'),'Compiled beta installer metadata differs.')
    p.require(receipt.get('version') == version and receipt.get('source_commit') == expected
              and receipt.get('setup_name') == setup_name and receipt.get('public_release_approved') is False
              and receipt.get('scientific_release_approved') is False and receipt.get('installed_acceptance')=='Not verified'
              and receipt.get('installer_app_id')==profile['app_id']
              and receipt.get('recipe_sha256') == recipe_sha(), 'Beta installer identity/source/recipe differs.')
    setup = folder/setup_name
    installer.pe(setup)
    p.require(receipt.get('setup_sha256') == installer.sha(setup)
              and setup.with_name(setup_name+'.sha256').read_text().strip() == installer.sha(setup)+'  '+setup_name,
              'Beta installer checksum differs.')
    p.require(receipt.get('files') == private.hashes(folder/'payload'), 'Beta payload changed after compilation.')
    payload(folder/'payload', expected, version)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('command', choices=['stage', 'record', 'verify'])
    parser.add_argument('--folder', type=Path, required=True)
    parser.add_argument('--source', required=True)
    parser.add_argument('--destination', type=Path)
    parser.add_argument('--runtime-report', type=Path)
    parser.add_argument('--compiler', type=Path)
    parser.add_argument('--prerequisite', type=Path)
    parser.add_argument('--tool-report', type=Path)
    parser.add_argument('--beta-version',default=VERSION)
    args = parser.parse_args()
    try:
        if args.command == 'stage':
            p.require(args.destination is not None and args.runtime_report is not None, 'Stage requires destination and runtime report.')
            stage(args.folder, args.destination, args.source, args.runtime_report,args.beta_version)
        elif args.command == 'record':
            p.require(all((args.compiler, args.prerequisite, args.tool_report)), 'Record requires actual compiler/prerequisite evidence.')
            record(args.folder, args.source, args.compiler, args.prerequisite, args.tool_report,args.beta_version)
        else:
            verify(args.folder, args.source,args.beta_version)
    except (ValueError, OSError, KeyError, TypeError, json.JSONDecodeError) as error:
        raise SystemExit(f'Beta installer refused: {error}')
