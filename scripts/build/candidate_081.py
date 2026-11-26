"""Opt-in private v0.8.1 preparation. Never installs, signs or publishes anything."""
import argparse
import json
import os
from pathlib import Path, PurePosixPath, PureWindowsPath
import re
import shutil
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT/'scripts/release'))
import private_validation_080 as existing

VERSION = '0.8.1-candidate'
PACKAGE = 'aeroscience-candidate-v0-8-1-toolbar'
PANEL = 'AeroScienceCandidateV081'
RECEIPT = 'CANDIDATE-081.json'
BASELINE = '8476c0f7a99f24cb7c1aa15e979efb8e45a5e845'
require, read, sha, inventory = existing.require, existing.read, existing.sha, existing.inventory


def source_check(source, repo=ROOT):
    existing.identities(source, '1'*32)
    def git(*args): return existing.command(['git','-C',repo,*args])
    require(git('rev-parse','HEAD') == source, 'Exact source SHA mismatch.')
    require(git('branch','--show-current') in ('develop/v0.8.1',''), 'Use develop/v0.8.1 or an explicitly pinned detached source.')
    require(not git('status','--porcelain','--untracked-files=all'), 'Preserve the dirty worktree; no candidate can be sealed.')
    require(git('rev-parse','--is-shallow-repository') == 'false', 'Full source history is required for P04 ancestry verification.')
    git('merge-base','--is-ancestor',BASELINE,source)


def prepare(output, source, instance):
    existing.identities(source, instance)
    source_check(source)
    output=existing.no_links(output)
    require(output.is_absolute() and output.name.startswith('Candidate081-') and output.parent.is_dir() and not output.exists(), 'Choose a fresh absolute Candidate081-* folder under an existing dedicated build parent.')
    protected=[ROOT,*existing.protected_roots()]
    if os.name=='nt':
        local=Path(os.environ['LOCALAPPDATA'])
        protected += [local/'AeroScience-Beta-v0.8.0',local/'AeroScience-Candidate-v0.8.1',local/'Programs']
    require(not any(existing.overlap(output,p) for p in protected), 'Output overlaps source, installed data or a protected application.')
    require(not any(p.name.startswith(('Candidate081-','Validation080-','Beta080-')) for p in output.parents), 'Do not nest or reuse an earlier attempt.')
    require(shutil.disk_usage(output.parent).free>=12*1024**3,'At least 12 GiB free is required for isolated publication.')
    output.mkdir()
    existing.write_new(output/'BUILD-IN-PROGRESS.json',dict(source_commit=source,instance=instance,completed=False))


def stage_toolbar(stage, source, instance):
    # Reuse the established full source/map/geography staging. Changes below apply
    # only to the fresh generated tree; no protected publisher/guard is modified.
    existing.stage_toolbar(stage,source,instance)
    replacements=[('AeroScience.Validation.0_8_0.','AeroScience.Candidate.0_8_1.'),
        ('AEROSCIENCE_VALIDATION_V080','AEROSCIENCE_CANDIDATE_V081'),('aeroscience-validation-v080','aeroscience-candidate-v081'),
        ('AeroScienceValidationV080',PANEL),('aeroscience_validation_v080','aeroscience_candidate_v081'),
        ('aeroscience-validation-v0-8-0-toolbar',PACKAGE),('AeroScience 0.8.0 Validation','AeroScience 0.8.1 Candidate'),
        ('validation+" 0.8.0','validation+" 0.8.1'),('profile+" 0.8.0','profile+" 0.8.1')]
    for path in stage.rglob('*'):
        if path.is_file() and path.suffix in ('.xml','.html','.js','.css','.json') and path.name not in ('AeroScience.Geography.js','geography-provenance.json'):
            text=path.read_text(encoding='utf-8-sig')
            for a,b in replacements: text=text.replace(a,b)
            if path.name=='toolbar-compatibility.json':
                data=json.loads(text);data.update(applicationVersion='0.8.1',minimumCompanionVersion='0.8.1');text=json.dumps(data)
            if path.parent.name=='PackageDefinitions':text=text.replace('AssetPackage Version="0.8.0"','AssetPackage Version="0.8.1"')
            path.write_text(text,encoding='utf-8',newline='\n')
    for a,b in [('PackageDefinitions/aeroscience-validation-v0-8-0-toolbar.xml','PackageDefinitions/'+PACKAGE+'.xml'),
        ('PackageSources/InGamePanels/aeroscience_validation_v080_panel.xml','PackageSources/InGamePanels/aeroscience_candidate_v081_panel.xml'),
        ('PackageSources/html_ui/InGamePanels/AeroScienceValidationV080','PackageSources/html_ui/InGamePanels/'+PANEL),
        ('PackageSources/html_ui/icons/toolbar/ICON_AEROSCIENCE_VALIDATION_V080.svg','PackageSources/html_ui/icons/toolbar/ICON_AEROSCIENCE_CANDIDATE_V081.svg')]:
        (stage/a).rename(stage/b)


def validate_sdk(folder, source, instance):
    found=inventory(folder);index={n.casefold():n for n in found}
    manifest=read(folder/index['manifest.json'])
    require(manifest.get('title')=='AeroScience 0.8.1 Candidate' and manifest.get('package_version')=='0.8.1' and manifest.get('dependencies')==[] and str(manifest.get('content_type','')).upper()=='MISC', 'Wrong candidate SDK package identity.')
    declared=set()
    for row in read(folder/index['layout.json'])['content']:
        name=row['path'].replace('\\','/');p=PurePosixPath(name);require(name and not p.is_absolute() and not PureWindowsPath(name).drive and '..' not in p.parts and p.as_posix()==name,'Unsafe SDK path.')
        key=name.casefold();require(key not in declared and key in index and key!='layout.json','Missing/duplicate SDK entry.')
        require(type(row['size']) is int and row['size']==(folder/index[key]).stat().st_size and type(row['date']) is int and row['date']>0,'SDK layout bytes differ.');declared.add(key)
    require(set(index)-{'layout.json','manifest.json'}<=declared,'SDK files are not indexed.')
    panel='html_ui/InGamePanels/'+PANEL+'/'
    required=['InGamePanels/aeroscience_candidate_v081_panel.spb','html_ui/icons/toolbar/ICON_AEROSCIENCE_CANDIDATE_V081.svg']+[panel+n for n in ['AeroScience.html','AeroScience.js','AeroScience.css','AeroScience.Map.js','AeroScience.Geography.js','geography-provenance.json','AeroScience.I18n.js','AeroScience.LocaleData.js','AeroScience.LocaleFormat.js','AeroScience.FlightStatus.js','toolbar-compatibility.json']]
    require({n.casefold() for n in required}<=index.keys(),'Incomplete candidate SDK output.')
    require(sha(folder/index[required[1].casefold()])==sha(ROOT/'assets/branding/orbital-arc-toolbar-white.svg'),'Candidate native icon differs from approved source.')
    descriptor=read(folder/index[(panel+'toolbar-compatibility.json').casefold()])
    require(all(descriptor.get(k)==v for k,v in dict(protocol=1,profile='validation',sourceCommit=source,validationInstance=instance,applicationVersion='0.8.1').items()),'Toolbar source/attempt mismatch.')
    script=(folder/index[(panel+'AeroScience.js').casefold()]).read_text(encoding='utf-8-sig')
    require(all(x in script for x in ['AeroScience.Candidate.0_8_1.Command.v1','AeroScience.Candidate.0_8_1.Reply.v1',source,instance]) and 'AeroScience.Validation.0_8_0.' not in script,'Wrong toolbar command authority.')
    registration=folder/index['ingamepanels/aeroscience_candidate_v081_panel.spb']
    require(registration.stat().st_size>32 and not registration.read_bytes().lstrip().startswith((b'<',b'{')),'Genuine SDK registration required.')
    return found


def payload_inventory(folder):
    result={}
    for name in ('application','toolbar-source'):
        require((folder/name).is_dir(),'Missing candidate component: '+name)
        result.update({name+'/'+key:value for key,value in inventory(folder/name).items()})
    if (folder/'toolbar-sdk.json').is_file():result['toolbar-sdk.json']=sha(folder/'toolbar-sdk.json')
    return dict(sorted(result.items()))


def record(folder, source, instance):
    source_check(source);existing.identities(source,instance)
    app=folder/'application';found=inventory(app)
    required={'AeroScience.exe','AeroScience.dll','AeroScience.Engine.dll','AeroScience.Missions.dll','AeroScience.Shared.dll','AeroScience.Telemetry.dll','AeroScience.XPlane.dll','AeroScience.SimConnect.dll','AeroScience.Hdf.SafeRead.dll','AeroScience.deps.json','coreclr.dll','System.Windows.Forms.dll','compiled-profile.json','startup-no-simconnect.json','lidar-check.json','native-dependencies.json','LidarRuntime/AeroScience.LidarReader.exe','LidarRuntime/build.json','LidarRuntime/LASzip.txt','LidarRuntime/NOTICE.md','wwwroot/i18n/locale-manifest.json'}
    require(required<=found.keys(),'Candidate publication is incomplete: '+str(sorted(required-found.keys())))
    for n in found:
        require(not n.endswith(('.db','.sqlite','.pdb')) and not n.endswith('settings.json'),'Owner/debug data cannot enter a candidate payload.')
        if n.lower().endswith('.exe'):require(n in ('AeroScience.exe','LidarRuntime/AeroScience.LidarReader.exe'),'Unexpected executable/launcher.')
    for name in ('AeroScience.exe','LidarRuntime/AeroScience.LidarReader.exe'):existing.pe_x64(app/name)
    proof=read(app/'compiled-profile.json')
    require(all(proof.get(k)==v for k,v in dict(profile='candidate081',version=VERSION,source_commit=source,instance=instance,port=5091,installed_acceptance='Not Tested').items()),'Compiled candidate identity proof mismatch.')
    require(proof['engine_sha256']==found['AeroScience.Engine.dll'] and proof['xplane_sha256']==found['AeroScience.XPlane.dll'],'Actual published assembly proof changed.')
    startup=read(app/'startup-no-simconnect.json')
    require(startup.get('source_commit')==source and startup.get('instance')==instance and startup.get('engine_sha256')==found['AeroScience.Engine.dll']
        and startup.get('simconnect_present') is False and startup.get('clean_shutdown')=='passed' and startup.get('offline_routes')=='passed','Actual published-runtime SimConnect-free startup proof required.')
    require(read(app/'lidar-check.json').get('raw_protocol_sha256')==read(ROOT/'tests/AeroScience.Tests/LidarOriginals/independent-reference.json')['nativeProtocolSha256'],'Actual retained LAZ decoder proof required.')
    deps=read(app/'AeroScience.deps.json')
    require(any(n.startswith('AeroScience.XPlane/') for n in deps['libraries']) and any(n.startswith('AeroScience.SimConnect/') for n in deps['libraries']),'Both adapters must remain in the dependency graph.')
    stamp=read(app/'LidarRuntime/build.json');expected=existing.lidar_source()
    require(stamp.get('source')==expected['source'] and stamp.get('sha256')==found['LidarRuntime/AeroScience.LidarReader.exe'],'Pinned native decoder differs.')
    require(sha(app/'LidarRuntime/LASzip.txt')==sha(ROOT/'native/lidar/laszip/COPYING.txt') and sha(app/'LidarRuntime/NOTICE.md')==sha(ROOT/'licenses/AeroScience.LidarReader.NOTICE.md'),'Native license/NOTICE differs.')
    dependencies=read(app/'native-dependencies.json')
    for name in ['LidarRuntime/AeroScience.LidarReader.exe']+(['SimConnect.dll'] if 'SimConnect.dll' in found else []):
        require(dependencies.get(name,{}).get('sha256')==found[name] and dependencies[name].get('imports'),'Native import/identity inspection missing.')
    manifest=read(app/'wwwroot/i18n/locale-manifest.json')
    require(manifest==read(ROOT/'src/AeroScience.Engine/wwwroot/i18n/locale-manifest.json'),'Candidate locale catalog is stale.')
    for name,relative in [('manifest','locale-manifest.json'),('schema','message-schema.json')]+[(v,'locales/'+v+'.json') for v in existing.LOCALES]:
        require(sha(app/'wwwroot/i18n'/relative)==sha(ROOT/'src/AeroScience.Engine/wwwroot/i18n'/relative)==proof['presentation_resources'][name],'Embedded/loose locale identity differs.')
    sdk=folder/'toolbar-sdk.json';sdk_proof=read(sdk) if sdk.exists() else None
    if sdk_proof:
        files=validate_sdk(app/'Toolbar'/PACKAGE,source,instance)
        require(sdk_proof['files']==files and sdk_proof['source_commit']==source and sdk_proof.get('instance')==instance and sdk_proof.get('variant')=='toolbar-candidate-081' and sdk_proof.get('source_dirty') is False and sdk_proof['sdk_contract']=='passed','SDK source/bytes proof differs.')
        require(all(re.fullmatch('[0-9a-f]{64}',sdk_proof.get(k,'')) for k in ['builder_sha256','sdk_header_sha256']),'Actual SDK input identities required.')
        from private_validation import validate_native_javascript
        validate_native_javascript(sdk_proof['native_javascript'],files)
        from package_release import validate_runtime
        validate_runtime(app,app/'runtime-check.json',app/'RUNTIME-AUTHORIZATION.json')
    else:require('SimConnect.dll' not in found,'An unverified native runtime cannot enter a structural-only candidate.')
    existing.write_new(folder/RECEIPT,dict(format='aeroscience-candidate-081',version=1,application_version=VERSION,source_commit=source,instance=instance,files=payload_inventory(folder),
        profile_folder='AeroScience-Candidate-v0.8.1/'+instance,port=5091,sdk_toolbar=bool(sdk_proof),public_release_approved=False,installed_acceptance='Not Tested'))


def verify(folder, source):
    source_check(source);receipt=read(folder/RECEIPT);files=payload_inventory(folder)
    require(receipt.get('format')=='aeroscience-candidate-081' and receipt.get('version')==1 and receipt.get('application_version')==VERSION and receipt.get('source_commit')==source and receipt.get('files')==files and receipt.get('public_release_approved') is False,'Candidate receipt/source/bytes mismatch.')
    existing.identities(source,receipt.get('instance'));return receipt


if __name__=='__main__':
    p=argparse.ArgumentParser();p.add_argument('command',choices=['prepare','stage-toolbar','record','verify']);p.add_argument('--folder',type=Path,required=True);p.add_argument('--source',required=True);p.add_argument('--instance');a=p.parse_args()
    try:
        if a.command=='prepare':prepare(a.folder,a.source,a.instance)
        elif a.command=='stage-toolbar':stage_toolbar(a.folder,a.source,a.instance)
        elif a.command=='record':record(a.folder,a.source,a.instance)
        else:verify(a.folder,a.source)
    except (ValueError,OSError,KeyError,subprocess.SubprocessError) as e:raise SystemExit('Candidate 081 refused: '+str(e))
