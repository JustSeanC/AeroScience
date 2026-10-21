"""Private validation packaging. Never finalizes or publishes a release."""
import argparse
import hashlib
import json
import shutil
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]

def hashes(folder):
    result = {}
    for path in sorted(folder.rglob('*')):
        if path.is_symlink():
            raise ValueError('Links are forbidden in validation artifacts')
        if path.is_file():
            result[path.relative_to(folder).as_posix()] = hashlib.sha256(path.read_bytes()).hexdigest()
    return result

def stage_toolbar(stage, commit):
    """Namespace generated sources BEFORE the genuine SDK compilation."""
    if len(commit)!=40 or any(c not in "0123456789abcdef" for c in commit): raise ValueError("An exact source commit is required")
    substitutions = [('AeroScience.Command.v1','AeroScience.Validation.0_5_0.Command.v1'),
                     ('AeroScience.Reply.v1','AeroScience.Validation.0_5_0.Reply.v1'),
                     ('PANEL_AEROSCIENCE','PANEL_AEROSCIENCE_VALIDATION'),
                     ('ICON_AEROSCIENCE','ICON_AEROSCIENCE_VALIDATION'),
                     ('ingamepanel-aeroscience','ingamepanel-aeroscience-validation'),
                     ('IngamePanelAeroScience','IngamePanelAeroScienceValidation'),
                     ('aeroscience_panel','aeroscience_validation_panel'),
                     ('InGamePanels/AeroScience/','InGamePanels/AeroScienceValidation/'),
                     ('aeroscience-toolbar.xml','aeroscience-validation-toolbar.xml'),
                     ('<Title>AeroScience</Title>','<Title>AeroScience 0.5.0 Validation</Title>'),
                     ('Name="AeroScience"','Name="AeroScience 0.5.0 Validation"'),
                     ('title="AeroScience"','title="AeroScience 0.5.0 Validation"'),
                     ('Windows engine unavailable. Launch AeroScience','Validation engine unavailable or wrong toolbar selected. Launch AeroScience 0.5.0 Validation')]
    for path in stage.rglob('*'):
        if path.is_file() and path.suffix in {'.xml','.html','.js','.json','.css'}:
            text = path.read_text(encoding='utf-8-sig')
            for old,new in substitutions:
                text = text.replace(old,new)
            if path.name=='AeroScience.js':
                text=text.replace('Object.assign({protocol:1,requestId,op}', 'Object.assign({protocol:1,requestId,op,validationSource:"'+commit+'"}')
                text=text.replace('const reply = JSON.parse(data);','const reply = JSON.parse(data); if(reply.error?.startsWith("Validation toolbar source mismatch")){this.pending.clear();this.setControls(false);this.renderIntelligence(false);this.status(reply.error);return;}')
            if path.name=='toolbar-compatibility.json':
                value=json.loads(text);value.update(profile='validation',sourceCommit=commit);text=json.dumps(value)
            path.write_text(text,encoding='utf-8')
    for old,new in [('PackageDefinitions/aeroscience-toolbar.xml','PackageDefinitions/aeroscience-validation-toolbar.xml'),
                    ('PackageSources/InGamePanels/aeroscience_panel.xml','PackageSources/InGamePanels/aeroscience_validation_panel.xml'),
                    ('PackageSources/html_ui/InGamePanels/AeroScience','PackageSources/html_ui/InGamePanels/AeroScienceValidation'),
                    ('PackageSources/html_ui/icons/toolbar/ICON_AEROSCIENCE.svg','PackageSources/html_ui/icons/toolbar/ICON_AEROSCIENCE_VALIDATION.svg')]:
        (stage/old).rename(stage/new)

def record(folder,commit):
    if len(commit)!=40 or any(c not in '0123456789abcdef' for c in commit):
        raise ValueError('An exact clean source commit is required')
    info = json.loads((folder/'build-info.json').read_text(encoding='utf-8-sig'))
    if info.get('profile')!='validation' or info.get('source_commit')!=commit or info.get('source_dirty') is not False:
        raise ValueError('Validation identity/source receipt missing')
    receipt = folder/'VALIDATION-RECEIPT.json'
    if receipt.exists():
        raise ValueError('An earlier receipt exists; use a new output folder')
    from package_release import validate_community
    validate_community(folder/'Toolbar/aeroscience-validation-toolbar',validation=True)
    sdk = json.loads((folder/'Toolbar/sdk-build.json').read_text(encoding='utf-8-sig'))
    if sdk.get('variant')!='toolbar-validation' or sdk.get('source_commit')!=commit or sdk.get('source_dirty') is not False or sdk.get('sdk_contract')!='passed':
        raise ValueError('Genuine exact-source validation SDK receipt missing')
    if sdk['files']!=hashes(folder/'Toolbar/aeroscience-validation-toolbar'):
        raise ValueError('SDK package bytes changed')
    receipt.write_text(json.dumps(dict(profile='validation',source_commit=commit,public_release_approved=False,
        installed_acceptance='Not tested',files=hashes(folder)),indent=2)+'\n',encoding='utf-8')

def verify(folder):
    receipt=json.loads((folder/'VALIDATION-RECEIPT.json').read_text(encoding='utf-8-sig'))
    found=hashes(folder); found.pop('VALIDATION-RECEIPT.json',None)
    if receipt.get('profile')!='validation' or receipt['files']!=found:
        raise ValueError('Validation files differ from the preserved receipt')
    print('Private validation bytes verified; installed/MSFS acceptance and public release are NOT approved.')

if __name__=='__main__':
    p=argparse.ArgumentParser();p.add_argument('command',choices=['stage-toolbar','record','verify']);p.add_argument('--folder',type=Path,required=True);p.add_argument('--commit');a=p.parse_args()
    if a.command=='stage-toolbar':stage_toolbar(a.folder,a.commit)
    elif a.command=='record':record(a.folder,a.commit)
    else:verify(a.folder)
