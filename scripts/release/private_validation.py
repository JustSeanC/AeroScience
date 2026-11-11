"""Private validation packaging. Never finalizes or publishes a release."""
import argparse
import hashlib
import json
import shutil
from pathlib import Path
from private_beta_identity import identity, LEGACY_VERSION, ENGINEERING_SOURCE

ROOT = Path(__file__).resolve().parents[2]

def hashes(folder):
    result = {}
    for path in sorted(folder.rglob('*')):
        if path.is_symlink():
            raise ValueError('Links are forbidden in validation artifacts')
        if path.is_file():
            result[path.relative_to(folder).as_posix()] = hashlib.sha256(path.read_bytes()).hexdigest()
    return result

def validate_native_javascript(proof, files):
    """Bind the ES2017 compiler/parser proof to the actual SDK-delivered JS bytes."""
    if not isinstance(proof,dict) or proof.get('target')!='ES2017' or proof.get('compiler')!='TypeScript 5.9.3' or proof.get('parser')!='Acorn 8.15.0':
        raise ValueError('Validated native ES2017 build proof missing')
    rows=proof.get('files')
    if not isinstance(rows,list) or not 1<=len(rows)<=12:raise ValueError('Invalid native JavaScript proof')
    expected={name:value for name,value in files.items() if name.endswith('.js')}
    declared={}
    for row in rows:
        name=row.get('path','')
        if not name.startswith('PackageSources/') or '..' in name.split('/') or not name.endswith('.js') or len(row.get('sourceSha256',''))!=64:
            raise ValueError('Invalid native JavaScript asset identity')
        name=name[len('PackageSources/'):]
        if name in declared:raise ValueError('Duplicate native JavaScript proof')
        declared[name]=row.get('compiledSha256')
    if declared!=expected:raise ValueError('SDK JavaScript bytes differ from the validated ES2017 output')

def stage_toolbar(stage, commit, beta_version=None):
    """Namespace generated sources BEFORE the genuine SDK compilation."""
    if len(commit)!=40 or any(c not in "0123456789abcdef" for c in commit): raise ValueError("An exact source commit is required")
    profile=identity(beta_version or LEGACY_VERSION)
    substitutions = [('AeroScience.Command.v1',profile['channel']+'.Command.v1'),
                     ('AeroScience.Reply.v1',profile['channel']+'.Reply.v1'),
                     ('PANEL_AEROSCIENCE',profile['panel']), ('ICON_AEROSCIENCE',profile['icon']),
                     ('ingamepanel-aeroscience',profile['tag']), ('IngamePanelAeroScience',profile['native_class']),
                     ('aeroscience_panel',profile['registration']),
                     ('InGamePanels/AeroScience/','InGamePanels/'+profile['toolbar_folder']+'/'),
                     ('aeroscience-toolbar.xml',profile['toolbar_package']+'.xml'),
                     ('<Title>AeroScience</Title>','<Title>'+profile['profile_title']+'</Title>'),
                     ('Name="AeroScience"','Name="'+profile['profile_title']+'"'),
                     ('title="AeroScience"','title="'+profile['profile_title']+'"'),
                     ('Windows engine unavailable. Launch AeroScience','Validation engine unavailable or wrong toolbar selected. Launch '+profile['profile_title'])]
    for path in stage.rglob('*'):
        if path.is_file() and path.suffix in {'.xml','.html','.js','.json','.css'}:
            text = path.read_text(encoding='utf-8-sig')
            for old,new in substitutions:
                text = text.replace(old,new)
            if path.name=='AeroScience.js':
                marker='const AEROSCIENCE_TOOLBAR_IDENTITY = { profile: "production", source: null };'
                if marker not in text: raise ValueError('Toolbar build identity marker missing')
                text=text.replace(marker, 'const AEROSCIENCE_TOOLBAR_IDENTITY = '+json.dumps(dict(profile='validation',source=commit))+';')
                text=text.replace('AEROSCIENCE_TOOLBAR_IDENTITY',profile['symbol'])
                text=text.replace('Object.assign({protocol:1,requestId,op}', 'Object.assign({protocol:1,requestId,op,validationSource:"'+commit+'"}')
                if "reply.error.indexOf('Validation toolbar source mismatch')===0" not in text:
                    raise ValueError('Toolbar validation source rejection handler missing')
            if path.name=='toolbar-compatibility.json':
                value=json.loads(text);value.update(profile='validation',sourceCommit=commit)
                if beta_version:value.update(applicationVersion=profile['version'],minimumCompanionVersion=profile['version'],privateBetaVersion=profile['version'])
                text=json.dumps(value)
            if beta_version and path.parent.name=='PackageDefinitions' and path.suffix=='.xml':
                import xml.etree.ElementTree as ET
                definition=ET.fromstring(text);definition.set('Version',profile['package_version']);text=ET.tostring(definition,encoding='unicode')
            if path.name=='AeroScience.html':
                text=text.replace('Toolbar build not initialized.',profile['profile_title']+' | source '+commit+' | toolbar initialization pending.')
            path.write_text(text,encoding='utf-8')
    for old,new in [('PackageDefinitions/aeroscience-toolbar.xml','PackageDefinitions/'+profile['toolbar_package']+'.xml'),
                    ('PackageSources/InGamePanels/aeroscience_panel.xml','PackageSources/InGamePanels/'+profile['registration']+'.xml'),
                    ('PackageSources/html_ui/InGamePanels/AeroScience','PackageSources/html_ui/InGamePanels/'+profile['toolbar_folder']),
                    ('PackageSources/html_ui/icons/toolbar/ICON_AEROSCIENCE.svg','PackageSources/html_ui/icons/toolbar/'+profile['icon']+'.svg')]:
        (stage/old).rename(stage/new)

def record(folder,commit,beta_version=None):
    if len(commit)!=40 or any(c not in '0123456789abcdef' for c in commit):
        raise ValueError('An exact clean source commit is required')
    info = json.loads((folder/'build-info.json').read_text(encoding='utf-8-sig'))
    if info.get('profile')!='validation' or info.get('source_commit')!=commit or info.get('source_dirty') is not False:
        raise ValueError('Validation identity/source receipt missing')
    receipt = folder/'VALIDATION-RECEIPT.json'
    if receipt.exists():
        raise ValueError('An earlier receipt exists; use a new output folder')
    from package_release import validate_community
    profile=identity(beta_version or LEGACY_VERSION)
    if beta_version:validate_beta_profile(folder,commit,beta_version)
    toolbar=folder/'Toolbar'/profile['toolbar_package']
    validate_community(toolbar,validation=True,private_beta_version=beta_version)
    sdk = json.loads((folder/'Toolbar/sdk-build.json').read_text(encoding='utf-8-sig'))
    if sdk.get('variant')!='toolbar-validation' or sdk.get('source_commit')!=commit or sdk.get('source_dirty') is not False or sdk.get('sdk_contract')!='passed':
        raise ValueError('Genuine exact-source validation SDK receipt missing')
    if beta_version and sdk.get('private_beta_version')!=beta_version:raise ValueError('Private beta SDK identity missing')
    if sdk['files']!=hashes(toolbar):
        raise ValueError('SDK package bytes changed')
    validate_native_javascript(sdk.get('native_javascript'),sdk['files'])
    value=dict(profile='validation',source_commit=commit,public_release_approved=False,installed_acceptance='Not tested',files=hashes(folder))
    if beta_version:value.update(version=beta_version,private_beta_version=beta_version,accepted_engineering_source=ENGINEERING_SOURCE,scientific_release_approved=False)
    receipt.write_text(json.dumps(value,indent=2)+'\n',encoding='utf-8')

def validate_beta_profile(folder,commit,version):
    profile=identity(version)
    info=json.loads((folder/'build-info.json').read_text(encoding='utf-8-sig'))
    proof=json.loads((folder/'PRIVATE-PROFILE.json').read_text(encoding='utf-8-sig'))
    if info.get('version')!=version or info.get('private_beta_version')!=version or info.get('source_commit')!=commit or info.get('public_release_approved') is not False or info.get('scientific_release_approved') is not False or info.get('installed_acceptance')!='Not tested':
        raise ValueError('Private beta version/source/public policy differs')
    if proof.get('version')!=version or proof.get('source_commit')!=commit or set(proof.get('binaries',{}))!={'AeroScience.dll','AeroScience.Engine.dll'}:
        raise ValueError('Compiled private beta version proof missing')
    for name,value in proof['binaries'].items():
        if value.get('product_version')!=version+'+'+commit or value.get('file_version')!='0.7.1.2' or value.get('sha256')!=hashlib.sha256((folder/name).read_bytes()).hexdigest():
            raise ValueError('Published beta binary version/source/hash differs: '+name)

def verify(folder,beta_version=None):
    receipt=json.loads((folder/'VALIDATION-RECEIPT.json').read_text(encoding='utf-8-sig'))
    found=hashes(folder); found.pop('VALIDATION-RECEIPT.json',None)
    if receipt.get('profile')!='validation' or receipt['files']!=found:
        raise ValueError('Validation files differ from the preserved receipt')
    if beta_version:
        if receipt.get('version')!=beta_version or receipt.get('private_beta_version')!=beta_version or receipt.get('accepted_engineering_source')!=ENGINEERING_SOURCE or receipt.get('public_release_approved') is not False or receipt.get('scientific_release_approved') is not False or receipt.get('installed_acceptance')!='Not tested':
            raise ValueError('Private beta source receipt approval/version differs')
        validate_beta_profile(folder,receipt.get('source_commit'),beta_version)
    print('Private validation bytes verified; installed/MSFS acceptance and public release are NOT approved.')

if __name__=='__main__':
    p=argparse.ArgumentParser();p.add_argument('command',choices=['identity','stage-toolbar','record','verify']);p.add_argument('--folder',type=Path);p.add_argument('--commit');p.add_argument('--beta-version');a=p.parse_args()
    if a.command=='identity':print(json.dumps(identity(a.beta_version or LEGACY_VERSION)))
    elif a.folder is None:p.error('--folder is required')
    elif a.command=='stage-toolbar':stage_toolbar(a.folder,a.commit,a.beta_version)
    elif a.command=='record':record(a.folder,a.commit,a.beta_version)
    else:verify(a.folder,a.beta_version)
