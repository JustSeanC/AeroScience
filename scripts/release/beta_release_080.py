"""Version-specific Beta 080 candidates. Never publishes or authorizes stable release."""
import argparse
import datetime as dt
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import shutil
import struct
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET

import package_release as release
import private_validation_080 as native
from private_validation import validate_native_javascript

ROOT=Path(__file__).resolve().parents[2]
VERSION='0.8.0-beta.1'
PACKAGE='aeroscience-beta-v0-8-0-toolbar'
PANEL='AeroScienceBetaV080'
TITLE='AeroScience v0.8.0 Beta 1'
SETUP='AeroScience-Setup-v'+VERSION+'.exe'
RECEIPT='BETA080-PAYLOAD.json'
BUILD='BETA080-INSTALLER.json'
REQUIRED={
    'identity':'Exact version/source/profile and clean candidate identity',
    'installer':'Install exact EXE in an isolated Windows environment; installed hashes match',
    'coexistence':'Existing apps, settings, Community packages and original data unchanged',
    'toolbar':'Actual MSFS 2024 SDK toolbar opens, connects, resizes/reopens and rejects wrong identity',
    'recording-science':'Basic actual recording; science arm/start/pause/resume/end stay independent',
    'genuine-sources':'Basic genuine retained terrain and historical LiDAR Results/provenance',
    'privacy':'Explicit source/download/deletion/sharing consent; no unauthorized transmission',
    'uninstall':'Uninstall candidate app only; beta data and other installations remain intact',
}
DEFERRED={
    'endurance':'60-minute+ flights, multi-leg/multi-session and extended resources',
    'large-data':'Larger genuine terrain/clouds and source inventories',
    'hardware':'Aircraft/GPU/hardware/regions/providers and prolonged interruptions/offline',
    'language-review':'Qualified fluent review of es-ES/fr-FR/de-DE/pt-BR candidates',
    'accessibility':'Broader installed High Contrast, screen-reader and usability evaluation',
}

require=native.require
read=native.read
sha=native.sha
inventory=native.inventory

def write(path,value):
    native.no_links(path)
    with Path(path).open('x',encoding='utf-8',newline='\n') as f:json.dump(value,f,indent=2,ensure_ascii=False,allow_nan=False);f.write('\n')

def identity():
    props=ET.parse(ROOT/'build/Beta080.props')
    require(props.findtext('.//Version')==VERSION and props.findtext('.//ReleaseChannel')=='beta'
        and props.findtext('.//AssemblyVersion')=='0.8.0.0' and props.findtext('.//FileVersion')=='0.8.0.1'
        and props.findtext('.//Product')==TITLE,'Beta build identity differs.')
    return dict(version=VERSION,package_version='0.8.0',channel='beta',profile='beta080',setup_name=SETUP,title=TITLE)

def safe_output(path,repo=ROOT,sdk=Path(r'C:\MSFS 2024 SDK')):
    path=native.no_links(path)
    require(path.is_absolute() and path.name.startswith('Beta080-') and not path.exists() and path.parent.is_dir(),'Choose a fresh absolute Beta080-<timestamp> output under an existing build parent.')
    extra=[]
    if os.name=='nt':
        local=Path(os.environ['LOCALAPPDATA']);extra=[local/'AeroScience-Beta-v0.8.0',local/'Programs/AeroScience-Beta-v0.8.0']
    for p in [repo,sdk,*native.protected_roots(),*extra]:require(not native.overlap(path,p),'Output overlaps source, SDK, Community, an installed app or data.')
    for ancestor in path.parents:
        require(not any((ancestor/n).exists() for n in ['AeroScience.exe',RECEIPT,BUILD,'build-info.json']), 'Do not nest inside an earlier build/installation.')
        require(ancestor.name.casefold() not in ['community','community2024','windows','program files','program files (x86)'],'Choose a dedicated build directory.')
        if ancestor.name.casefold()=='aeroscience-builds':require(path.parent==ancestor,'Never nest in a prior AeroScience-Builds attempt.')
    return path

def preflight(commit,sdk,approval,output,confirmed):
    # Reuse the actual SDK/compiler/runtime/source probes, not their private
    # package identity. Both prospective paths have the same disk parent.
    shim=Path(output).parent/('Validation080-BetaPreflight-'+__import__('uuid').uuid4().hex)
    proof=native.preflight(ROOT,commit,sdk,approval,shim,confirmed)
    for check in proof['checks']:
        if check['name']=='output-isolation':
            try:check.update(outcome='Passed',evidence=str(safe_output(output,sdk=sdk)))
            except (ValueError,OSError) as e:check.update(outcome='Blocked',next_action=str(e));check.pop('evidence',None)
    proof.update(profile='beta080',version=VERSION,ready=all(c['outcome']=='Passed' for c in proof['checks']))
    return proof

def stage_toolbar(stage,commit):
    # Reuse the complete source, SDK bootstrap, offline geography and ES2017
    # staging; replace only the explicitly separate profile identities.
    native.stage_toolbar(stage,commit,'1'*32)
    stage=Path(stage)
    for path in stage.rglob('*'):
        if not path.is_file() or path.suffix not in ['.js','.html','.css','.xml','.json']:continue
        text=path.read_text(encoding='utf-8-sig')
        for a,b in [(native.PACKAGE,PACKAGE),(native.PANEL,PANEL),(native.TITLE,TITLE),('aeroscience_validation_v080_panel','aeroscience_beta_v080_panel'),
                    ('AEROSCIENCE_VALIDATION_V080','AEROSCIENCE_BETA_V080'),('AeroScience.Validation.0_8_0.','AeroScience.Beta.0_8_0.'),
                    ('ingamepanel-aeroscience-validation-v080','ingamepanel-aeroscience-beta-v080'),('IngamePanelAeroScienceValidationV080','IngamePanelAeroScienceBetaV080')]:text=text.replace(a,b)
        if path.name=='AeroScience.js':
            text=text.replace('validationSource:"'+commit+'",validationInstance:"'+'1'*32+'"','betaSource:"'+commit+'",betaVersion:"'+VERSION+'"')
            text=text.replace('"profile": "validation", "source": "'+commit+'", "instance": "'+'1'*32+'"','"profile": "beta080", "source": "'+commit+'"')
            text=text.replace(' 0.8.0 | attempt '+'1'*8,' '+VERSION)
        if path.name=='toolbar-compatibility.json':
            data=json.loads(text);data.pop('validationInstance',None);data.update(profile='beta080',applicationVersion=VERSION,minimumCompanionVersion='0.8.0',channel='beta',sourceCommit=commit);text=json.dumps(data)
        text=text.replace(' | attempt '+'1'*32,'')
        path.write_text(text,encoding='utf-8',newline='\n')
    renames=[('PackageDefinitions/'+native.PACKAGE+'.xml','PackageDefinitions/'+PACKAGE+'.xml'),
        ('PackageSources/InGamePanels/aeroscience_validation_v080_panel.xml','PackageSources/InGamePanels/aeroscience_beta_v080_panel.xml'),
        ('PackageSources/html_ui/InGamePanels/'+native.PANEL,'PackageSources/html_ui/InGamePanels/'+PANEL),
        ('PackageSources/html_ui/icons/toolbar/ICON_AEROSCIENCE_VALIDATION_V080.svg','PackageSources/html_ui/icons/toolbar/ICON_AEROSCIENCE_BETA_V080.svg')]
    for a,b in renames:(stage/a).rename(stage/b)

def community(folder,commit):
    found=inventory(folder);indexed={name.casefold():name for name in found}
    require({'manifest.json','layout.json'}<=indexed.keys(),'Missing genuine SDK manifest/layout output.')
    def actual(name):
        require(name.casefold() in indexed,'Missing native package file: '+name)
        return folder/indexed[name.casefold()]
    manifest=read(actual('manifest.json'))
    require(manifest.get('title')==TITLE and manifest.get('package_version')=='0.8.0' and manifest.get('dependencies')==[]
        and str(manifest.get('content_type','')).upper()=='MISC','Wrong Beta SDK manifest.')
    layout=read(actual('layout.json'));seen=set()
    for item in layout['content']:
        raw=item.get('path');require(isinstance(raw,str) and raw,'Missing SDK layout path.')
        name=raw.replace('\\','/');p=PurePosixPath(name);key=name.casefold()
        # The Windows SDK may lowercase layout names while retaining case on
        # disk. Match the established private verifier's identity rules; never
        # rewrite the SDK layout, package bytes or receipt to make them agree.
        require(not p.is_absolute() and '..' not in p.parts and ':' not in name and p.as_posix()==name
            and key not in seen and key!='layout.json','Unsafe/duplicate SDK layout path: '+raw)
        require(key in indexed,'SDK layout file is missing: '+raw)
        size=actual(name).stat().st_size
        require(type(item.get('size')) is int and item['size']==size,
            'SDK layout size differs: '+raw+'; expected '+str(item.get('size'))+', actual '+str(size))
        require(type(item.get('date')) is int and item['date']>0,'SDK layout date is invalid: '+raw+'; date '+str(item.get('date')))
        seen.add(key)
    require(indexed.keys()-{'layout.json','manifest.json'}<=seen,'Undeclared native package files.')
    panel='html_ui/InGamePanels/'+PANEL+'/'
    descriptor=read(actual(panel+'toolbar-compatibility.json'))
    require(all(descriptor.get(k)==v for k,v in {'profile':'beta080','applicationVersion':VERSION,'minimumCompanionVersion':'0.8.0','channel':'beta','sourceCommit':commit,'protocol':1}.items()),'Beta toolbar compatibility/source mismatch.')
    registration=actual('InGamePanels/aeroscience_beta_v080_panel.spb');b=registration.read_bytes()
    require(len(b)>32 and not b.lstrip().startswith((b'<',b'{')),'An actual SDK-compiled registration is required.')
    script=actual(panel+'AeroScience.js').read_text(encoding='utf-8-sig')
    require(all(v in script for v in ['AeroScience.Beta.0_8_0.Command.v1','AeroScience.Beta.0_8_0.Reply.v1'])
        and re.search(r'\bbetaSource\s*:\s*"'+commit+'"',script) and re.search(r'\bbetaVersion\s*:\s*"'+re.escape(VERSION)+'"',script)
        and not re.search(r'\bvalidation(?:Source|Instance)\s*:',script),'Foreign command channel/identity.')
    require('panel-id="PANEL_AEROSCIENCE_BETA_V080"' in actual(panel+'AeroScience.html').read_text(encoding='utf-8-sig'),'SDK TemplateElement/bootstrap identity missing.')
    require(sha(actual('html_ui/icons/toolbar/ICON_AEROSCIENCE_BETA_V080.svg'))==sha(ROOT/'assets/branding/orbital-arc-toolbar-white.svg'),'Toolbar icon changed.')
    for name in ['AeroScience.I18n.js','AeroScience.LocaleData.js','AeroScience.LocaleFormat.js','AeroScience.Map.js','AeroScience.Geography.js','AeroScience.FlightStatus.js','AeroScience.css']:
        actual(panel+name)
    return found

def imports(path):
    """Inspect native x64 PE imports independently of the dumpbin report."""
    native.pe_x64(path,dll=Path(path).suffix.lower()=='.dll');data=Path(path).read_bytes();pe=struct.unpack_from('<I',data,60)[0]
    count=struct.unpack_from('<H',data,pe+6)[0];size=struct.unpack_from('<H',data,pe+20)[0];optional=pe+24
    require(1<=count<=96 and size>=240,'Invalid native section/optional-header bounds.')
    require(optional+size<=len(data),'Truncated PE optional header.')
    # Delay imports need separate dependency review; never silently omit them.
    require(struct.unpack_from('<II',data,optional+112+13*8)==(0,0),'Delayed native dependencies require explicit review.')
    def offset(rva):
        for i in range(count):
            pos=optional+size+i*40;require(pos+40<=len(data),'Truncated PE sections.')
            virtual,base,raw_size,raw=struct.unpack_from('<IIII',data,pos+8)
            if base<=rva<base+max(virtual,raw_size):
                value=raw+rva-base;require(rva-base<raw_size and value<len(data),'Unbacked native import RVA.');return value
        raise ValueError('Unmapped native import RVA.')
    rva,length=struct.unpack_from('<II',data,optional+112+8)
    require(rva and 20<=length<=65536,'Native import directory missing/out of bounds.')
    names=[];base=offset(rva)
    for i in range(min(128,length//20)):
        pos=base+i*20;require(pos+20<=len(data),'Truncated native import descriptor.')
        values=struct.unpack_from('<IIIII',data,pos)
        if not any(values):return sorted(set(names))
        start=offset(values[3]);end=data.find(b'\0',start,start+256);require(end>start,'Invalid import name.')
        name=data[start:end].decode('ascii').lower();require(re.fullmatch('[a-z0-9_.-]+\\.dll',name),'Invalid DLL import.');names.append(name)
    raise ValueError('Too many native dependencies.')

def native_integrity(folder,execute=False):
    info=read(folder/'build-info.json');identity()
    require(info.get('profile')=='beta080' and info.get('version')==VERSION and info.get('channel')=='beta'
        and info.get('source_dirty') is False and re.fullmatch('[0-9a-f]{40}',info.get('source_commit','')),'Only the explicit clean Beta 080 graph may admit its native decoder.')
    profile=read(folder/'compiled-profile.json')
    require(all(profile.get(k)==v for k,v in {'profile':'beta080','version':VERSION,'channel':'beta','source_commit':info['source_commit'],'port':5089,'mutex':r'Local\AeroScience.Desktop','command_event':'AeroScience.Beta.0_8_0.Command.v1','reply_event':'AeroScience.Beta.0_8_0.Reply.v1','cookie':'AeroScience.Beta.0_8_0.Session'}.items()),'Compiled Beta profile differs.')
    require(profile.get('engine_sha256')==sha(folder/'AeroScience.Engine.dll'),'Published Engine differs from inspected assembly.')
    require(profile.get('desktop_sha256')==sha(folder/'AeroScience.exe') and profile.get('desktop_file_version')=='0.8.0.1'
        and profile.get('desktop_product_version')==VERSION+'+'+info['source_commit'] and profile.get('desktop_product_name')==TITLE,
        'Actual Windows EXE differs from its inspected Beta version/product metadata.')
    require(profile.get('configuration_directory','').replace('\\','/').endswith('/AeroScience-Beta-v0.8.0'),'Beta data root differs.')
    source=native.lidar_source();stamp=read(folder/'LidarRuntime/build.json');executable=folder/'LidarRuntime/AeroScience.LidarReader.exe'
    require(stamp.get('source')==source['source'] and stamp.get('upstreamCommit')==source['upstreamCommit'] and stamp.get('sha256')==sha(executable)
        and profile.get('lidar_source')==source['source'] and profile.get('lidar_sha256')==sha(executable),'Pinned/embedded native decoder identity mismatch.')
    for name,original in [('LASzip.txt','native/lidar/laszip/COPYING.txt'),('NOTICE.md','licenses/AeroScience.LidarReader.NOTICE.md'),('AUTHORS.txt','native/lidar/laszip/AUTHORS.txt'),('source-policy.json','native/lidar/source-policy.json')]:
        require(sha(folder/'LidarRuntime'/name)==sha(ROOT/original),'Native source/license/notice differs: '+name)
    dependencies=read(folder/'native-dependencies.json');policy=dependencies.get('signing_policy')
    require(policy in ['require-signed','allow-unsigned-beta'],'Explicit beta signing policy required.')
    for name in ['AeroScience.exe','SimConnect.dll','LidarRuntime/AeroScience.LidarReader.exe']:
        proof=dependencies['files'][name];require(proof['sha256']==sha(folder/name),'Signature/dependency proof hash differs.')
        allowed=['Valid'] if policy=='require-signed' else ['Valid','NotSigned']
        require(proof['signature_status'] in allowed,'Signature does not satisfy the recorded beta policy.')
        if name!='AeroScience.exe':require(sorted(n.lower() for n in proof['imports'])==imports(folder/name),'Actual PE imports differ from dependency report.')
    allowed_system={'kernel32.dll','user32.dll','advapi32.dll','ntdll.dll','ucrtbase.dll','vcruntime140.dll','vcruntime140_1.dll','msvcp140.dll','ws2_32.dll','bcrypt.dll','ole32.dll','oleaut32.dll','shell32.dll','secur32.dll','shlwapi.dll','version.dll','winmm.dll','imm32.dll','combase.dll','gdi32.dll'}
    for name in ['SimConnect.dll','LidarRuntime/AeroScience.LidarReader.exe']:
        require(all(n in allowed_system or n.startswith('api-ms-win-') or n.startswith('ext-ms-win-') for n in imports(folder/name)), 'Unreviewed native DLL dependency. Do not substitute a library.')
    result=read(folder/'lidar-check.json');reference=read(ROOT/'tests/AeroScience.Tests/LidarOriginals/independent-reference.json')
    require(result.get('platform')=='Windows' and result.get('points')==34923 and result.get('raw_protocol_sha256')==reference['nativeProtocolSha256'],'Actual genuine Windows decoder check missing.')
    if execute:require(native.decoder_probe(executable)==result,'Repeated actual decoder result differs.')
    return source

def payload(folder,commit,execute=False):
    folder=native.no_links(folder);native_integrity(folder,execute)
    require(read(folder/'build-info.json')['source_commit']==commit,'Beta source mismatch.')
    windows_payload(folder)
    runtime_disclosure(folder)
    sdk=read(folder/'Toolbar/sdk-build.json');files=community(folder/'Toolbar'/PACKAGE,commit)
    require(sdk.get('variant')=='toolbar-beta-080' and sdk.get('version')==VERSION and sdk.get('source_commit')==commit
        and sdk.get('source_dirty') is False and sdk.get('sdk_contract')=='passed' and sdk.get('files')==files
        and all(re.fullmatch('[0-9a-f]{64}',sdk.get(k,'')) for k in ['builder_sha256','sdk_header_sha256']),'Genuine matching SDK proof required.')
    validate_native_javascript(sdk.get('native_javascript'),files)
    manifest=read(folder/'wwwroot/i18n/locale-manifest.json');require(manifest==read(ROOT/'src/AeroScience.Engine/wwwroot/i18n/locale-manifest.json'),'Stale locale identity.')
    panel=folder/'Toolbar'/PACKAGE/'html_ui/InGamePanels'/PANEL
    text=(panel/'AeroScience.LocaleData.js').read_text(encoding='utf-8-sig');data=json.JSONDecoder().raw_decode(text[text.index('=')+1:].lstrip())[0]
    generated=folder/'wwwroot/i18n/catalog.generated.js'
    require(sha(generated)==sha(ROOT/'src/AeroScience.Engine/wwwroot/i18n/catalog.generated.js'),'Generated browser catalog differs from this source.')
    full_text=generated.read_text(encoding='utf-8-sig');full=json.JSONDecoder().raw_decode(full_text[full_text.index('=')+1:].lstrip())[0]
    locale_projection(data,full)
    embedded=read(folder/'compiled-profile.json')['presentation_resources']
    for name,relative in [('manifest','locale-manifest.json'),('schema','message-schema.json')]+[(n,'locales/'+n+'.json') for n in native.LOCALES]:
        require(sha(folder/'wwwroot/i18n'/relative)==sha(ROOT/'src/AeroScience.Engine/wwwroot/i18n'/relative)==embedded.get(name),'Bundled/embedded locale mismatch.')
    require(sha(folder/'LICENSE')==sha(ROOT/'LICENSE') and sha(folder/'THIRD-PARTY-NOTICES.md')==sha(ROOT/'THIRD-PARTY-NOTICES.md'),'Canonical license inventory changed.')
    return inventory(folder,(RECEIPT,))

def locale_projection(data,full):
    """Verify projected bytes, not merely a copied catalog identity label."""
    require(data['manifest']==full['manifest'] and data['schema']['version']==full['schema']['version'],'Native catalog differs from companion.')
    keys=set(data['schema']['messages']);canonical=full['schema']['messages']
    required={k for k in canonical if re.match(r'^(toolbar|native|map\.native|common|science|nav|localization)\.',k)}
    require(required<=keys<=canonical.keys() and all(data['schema']['messages'][k]==canonical[k] for k in keys),'Native message/schema projection differs.')
    require(set(data['catalogs'])==set(full['catalogs'])==set(native.LOCALES),'Native locale choices differ.')
    for name,catalog in data['catalogs'].items():
        original=full['catalogs'][name]
        require(all(catalog.get(k)==original.get(k) for k in ['version','locale','sourceHash'])
            and catalog.get('pendingEnglishFallbackKeys',[])==[k for k in original.get('pendingEnglishFallbackKeys',[]) if k in keys]
            and catalog['messages']=={k:v for k,v in original['messages'].items() if k in keys},'Native translated messages/fallback differ: '+name)

def runtime_disclosure(folder):
    report,public=release.validate_runtime(folder,folder/'runtime-check.json',folder/'RUNTIME-AUTHORIZATION.json')
    require(set(report)<= {'checked_utc','sha256','file_version','architecture','export_check','exports'},
        'Keep absolute SDK paths and private environment details out of the distributed runtime report.')
    require(read(folder/'RUNTIME-AUTHORIZATION.json')==public,'Only the existing public runtime authorization projection may ship; retain correspondence privately.')

def windows_payload(folder):
    # Composition of two strict checks, without changing the byte-protected
    # legacy public gate: validate the one new native compartment completely,
    # then run that original gate on every other file. Unknown EXEs are kept
    # in the projection and rejected. Hard links are read-only here; cross-
    # volume systems copy bytes. Nothing in the source payload is edited.
    native_integrity(folder)
    with tempfile.TemporaryDirectory(prefix='aeroscience-beta080-verify-') as work:
        shadow=Path(work)
        for name in inventory(folder):
            if name=='LidarRuntime/AeroScience.LidarReader.exe':continue
            source=folder/name;target=shadow/name;target.parent.mkdir(parents=True,exist_ok=True)
            try:os.link(source,target)
            except OSError:shutil.copyfile(source,target)
        release.validate_windows_payload(shadow,require_simconnect=True)

def seal(folder,commit):
    require(os.name=='nt','Payload sealing requires actual Windows execution.')
    native.source_check(ROOT,commit);found=payload(folder,commit,True)
    value={**identity(),'schema':'aeroscience-beta080-payload-1','source_commit':commit,'files':found,'built_utc':native.utc(),
        'installed_acceptance':'Not Tested','publication_approved':False,'stable_release_approved':False}
    write(folder/RECEIPT,value);return sha(folder/RECEIPT)

def verify(folder,commit):
    value=read(folder/RECEIPT)
    require(value.get('schema')=='aeroscience-beta080-payload-1' and value.get('version')==VERSION and value.get('source_commit')==commit
        and value.get('publication_approved') is False and value.get('files')==payload(folder,commit),'Candidate payload receipt/bytes differ.')
    return value

def acceptance(source,setup_sha,receipt_sha):
    return {'schema':'aeroscience-beta080-acceptance-1','version':VERSION,'source_commit':source,'installer_sha256':setup_sha,'installer_receipt_sha256':receipt_sha,
        'publication_approved':False,'tests':{k:{'name':v,'outcome':'Not Tested','notes':''} for k,v in REQUIRED.items()},
        'deferred_beta_testing':{k:{'name':v,'outcome':'Deferred'} for k,v in DEFERRED.items()},'critical_defects':[]}

def capture_ci(run_id,commit):
    import urllib.request
    require(str(run_id).isdigit() and re.fullmatch('[0-9a-f]{40}',commit),'Exact CI run/source required.')
    base='https://api.github.com/repos/JustSeanC/AeroScience/actions/runs/'+str(run_id)
    def get(url):
        request=urllib.request.Request(url,headers={'Accept':'application/vnd.github+json','User-Agent':'AeroScience-Beta080-build'})
        with urllib.request.urlopen(request,timeout=30) as response:
            body=response.read(4*1024*1024+1);require(len(body)<=4*1024*1024,'CI API response is oversized.');return json.loads(body)
    run=get(base);jobs=get(base+'/jobs?per_page=100')['jobs'];passed=[j['name'] for j in jobs if j['conclusion']=='success']
    require(run['head_sha']==commit and run['conclusion']=='success' and run['path']=='.github/workflows/ci.yml'
        and {'linux','windows-companion','organization-deployment'}<=set(passed),'A successful complete Full CI on this exact source is required; focused CI cannot substitute.')
    return {'schema':'aeroscience-beta080-ci-1','source_commit':commit,'scope':'full','conclusion':'success','run_id':int(run_id),
        'url':run['html_url'],'passed_jobs':passed,'checked_utc':native.utc()}

def record_installer(folder,commit,compiler,vc,tools_report):
    from installer_release import pe
    require(os.name=='nt','Installer recording requires actual Windows compilation/signature inspection.')
    payload_dir=folder/'payload/application';seal_value=verify(payload_dir,commit)
    setup=folder/SETUP;pe(setup);pe(vc);report=read(tools_report)
    require(report.get('vc_sha256')==sha(vc) and report.get('vc_signature')=='Valid' and 'Microsoft Corporation' in report.get('vc_publisher','')
        and report.get('vc_architecture')=='x64' and report.get('vc_version'),'A signed matching Microsoft VC v14 x64 prerequisite is mandatory.')
    require(report.get('compiler_sha256')==sha(compiler) and report.get('compiler_version'),'Compiler report differs.')
    policy=read(payload_dir/'native-dependencies.json')['signing_policy'];signature=report.get('setup_signature')
    require(report.get('setup_sha256')==sha(setup),'Signature report identifies different installer bytes.')
    require(signature in (['Valid'] if policy=='require-signed' else ['Valid','NotSigned']),'Installer signature does not satisfy the recorded policy.')
    value={**identity(),'schema':'aeroscience-beta080-installer-1','source_commit':commit,'installer_sha256':sha(setup),
        'payload_receipt_sha256':sha(payload_dir/RECEIPT),'files':inventory(folder/'payload'),'compiler':report,
        'recipe_sha256':sha(ROOT/'scripts/installer/AeroScienceBeta080.iss'),'signing_policy':policy,
        'setup_signature':signature,'installed_acceptance':'Not Tested','publication_approved':False}
    write(folder/BUILD,value);(folder/(SETUP+'.sha256')).write_text(sha(setup)+'  '+SETUP+'\n',encoding='ascii',newline='\n')
    write(folder/'ACCEPTANCE.json',acceptance(commit,sha(setup),sha(folder/BUILD)))
    verify_installer(folder,commit)
    return value

def verify_installer(folder,commit):
    from installer_release import pe
    value=read(folder/BUILD);pe(folder/SETUP)
    require(value.get('schema')=='aeroscience-beta080-installer-1' and value.get('version')==VERSION
        and value.get('channel')=='beta' and value.get('source_commit')==commit and value.get('publication_approved') is False,'Foreign installer receipt/version/channel.')
    policy=read(folder/'payload/application/native-dependencies.json')['signing_policy'];tools=value.get('compiler',{})
    require(value.get('signing_policy')==policy and value.get('setup_signature')==tools.get('setup_signature')
        and value.get('setup_signature') in (['Valid'] if policy=='require-signed' else ['Valid','NotSigned'])
        and tools.get('vc_signature')=='Valid' and 'Microsoft Corporation' in tools.get('vc_publisher','')
        and tools.get('vc_architecture')=='x64','Installer signature/prerequisite policy changed.')
    require(value.get('installer_sha256')==sha(folder/SETUP)==tools.get('setup_sha256')
        and (folder/(SETUP+'.sha256')).read_text(encoding='ascii').strip()==sha(folder/SETUP)+'  '+SETUP,'Installer bytes/checksum changed.')
    require(value.get('recipe_sha256')==sha(ROOT/'scripts/installer/AeroScienceBeta080.iss'),'Installer recipe changed; use its exact source checkout.')
    require(value.get('files')==inventory(folder/'payload') and value.get('payload_receipt_sha256')==sha(folder/'payload/application'/RECEIPT),'Installer payload changed.')
    verify(folder/'payload/application',commit)
    return value

def verify_installed(folder,installed,commit):
    require(os.name=='nt','Installed byte/decoder verification requires actual Windows execution.')
    value=verify_installer(folder,commit);expected={n.removeprefix('application/'):s for n,s in value['files'].items() if n.startswith('application/')}
    require(inventory(installed/'application')==expected,'Installed application bytes differ; do not use earlier acceptance.')
    native_integrity(installed/'application',execute=os.name=='nt')
    return value

def readiness(folder,commit,acceptance_path=None,ci=None):
    installer=None;failures=[]
    try:installer=verify_installer(folder,commit)
    except (ValueError,OSError,KeyError) as e:failures.append(str(e))
    tests={k:{'name':v,'outcome':'Not Tested','notes':''} for k,v in REQUIRED.items()}
    critical=[]
    if acceptance_path and installer:
        observed=read(acceptance_path)
        require(observed.get('schema')=='aeroscience-beta080-acceptance-1' and observed.get('version')==VERSION
            and observed.get('source_commit')==commit and observed.get('installer_sha256')==installer['installer_sha256']
            and observed.get('installer_receipt_sha256')==sha(folder/BUILD),'Acceptance does not identify these exact installer bytes.')
        require(set(observed['tests'])==set(REQUIRED),'Required beta acceptance checks cannot be removed or replaced by deferred work.')
        for key,value in observed['tests'].items():
            require(value.get('outcome') in ['Passed','Failed','Blocked','Not Tested'],'Invalid beta observation outcome.')
            require(value['outcome']!='Passed' or value.get('personally_observed') is True and len(value.get('notes','').strip())>=12,'A pass requires actual personal observation and substantive notes.')
        tests=observed['tests'];critical=observed.get('critical_defects',[])
    automation={'outcome':'Not Tested','name':'Full CI on exact candidate runtime'}
    if ci:
        proof=read(ci)
        if (proof.get('source_commit')==commit and proof.get('conclusion')=='success' and proof.get('scope')=='full'
            and {'linux','windows-companion','organization-deployment'}<=set(proof.get('passed_jobs',[]))):
            automation={'name':automation['name'],'outcome':'Passed','run_url':proof.get('url')}
        else:automation={'name':automation['name'],'outcome':'Blocked','reason':'CI scope/source/conclusion differs.'}
    # Broad performance/translation cards are deliberately not release blockers.
    presented={k:({**v,'observation_outcome':'Failed','outcome':'Blocked'} if v['outcome']=='Failed' else v) for k,v in tests.items()}
    return {'schema':'aeroscience-beta080-readiness-1','version':VERSION,'channel':'beta','source_commit':commit,
        'installer_sha256':installer['installer_sha256'] if installer else None,
        'distribution_ready':bool(installer) and not critical and automation['outcome']=='Passed' and all(v['outcome']=='Passed' for v in tests.values()),
        'required_pre_beta':{'packaging':{'outcome':'Blocked' if failures else 'Passed','problems':failures},'automation':automation,**presented},
        'deferred_beta_testing':{k:{'name':v,'outcome':'Deferred'} for k,v in DEFERRED.items()},
        'critical_defects':critical,'publication_approved':False,'stable_release_approved':False}

def main():
    parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('action',choices=['metadata','preflight','stage-toolbar','verify-toolbar','seal','verify','record-installer','verify-installer','verify-installed','readiness','capture-ci'])
    parser.add_argument('--source');parser.add_argument('--folder',type=Path);parser.add_argument('--output',type=Path);parser.add_argument('--sdk',type=Path);parser.add_argument('--approval',type=Path);parser.add_argument('--confirmed',action='store_true');parser.add_argument('--report',type=Path)
    for name in ['compiler','vc','tool-report','installed','acceptance','ci']:parser.add_argument('--'+name,type=Path)
    parser.add_argument('--run-id')
    args=parser.parse_args()
    try:
        if args.action=='metadata':value=identity()
        elif args.action=='capture-ci':value=capture_ci(args.run_id,args.source)
        elif args.action=='preflight':
            value=preflight(args.source,args.sdk,args.approval,args.output,args.confirmed)
            if args.report:write(args.report,value)
            print(json.dumps(value,indent=2));return 0 if value['ready'] else 1
        elif args.action=='stage-toolbar':stage_toolbar(args.folder,args.source);value={'stage':str(args.folder)}
        elif args.action=='verify-toolbar':
            found=community(args.folder,args.source)
            value={'outcome':'Passed','scope':'Read-only SDK layout and beta toolbar identity checks; not complete payload, signing or installed acceptance.',
                'source_commit':args.source,'files_verified':len(found),'installed_acceptance':'Not Tested','publication_approved':False}
        elif args.action=='seal':value={'receipt_sha256':seal(args.folder,args.source)}
        elif args.action=='verify':value=verify(args.folder,args.source)
        elif args.action=='record-installer':value=record_installer(args.folder,args.source,args.compiler,args.vc,args.tool_report)
        elif args.action=='verify-installer':value=verify_installer(args.folder,args.source)
        elif args.action=='verify-installed':value=verify_installed(args.folder,args.installed,args.source)
        else:value=readiness(args.folder,args.source,args.acceptance,args.ci)
        if args.report:write(args.report,value)
        print(json.dumps(value,indent=2))
    except (ValueError,OSError,KeyError,TypeError,subprocess.SubprocessError,json.JSONDecodeError) as e:parser.exit(1,'Beta 080 refused: '+str(e)+'\n')
    return 0

if __name__=='__main__':sys.exit(main())
