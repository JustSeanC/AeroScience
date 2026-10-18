"""Automatic technical alpha validation. No manual acceptance file and no hardware-pass claims."""
import argparse
from datetime import datetime,timezone
import hashlib
import json
from pathlib import Path
import shutil
import tempfile
import xml.etree.ElementTree as xml
import zipfile
import package_release as p
import installer_release as installer

def digest(path):
    with path.open('rb') as file:return hashlib.file_digest(file,'sha256').hexdigest()

def evidence(unit,browser,helpers,recovery,commit,output):
    p.require(p.valid_commit(commit),'A clean source commit is required.')
    counters=xml.parse(unit).find('.//{*}Counters')
    p.require(counters is not None,'The .NET TRX counters are missing.')
    dotnet={name:int(counters.get(name,0)) for name in ('total','passed','failed','error','aborted','notExecuted')}
    web=p.read_json(browser).get('stats',{});helper=p.read_json(helpers)
    p.require(dotnet['passed']>0 and dotnet['failed']==dotnet['error']==dotnet['aborted']==0,'Actual .NET tests failed or did not execute.')
    p.require(web.get('expected',0)>0 and web.get('unexpected',-1)==0 and web.get('flaky',0)==0,'Actual browser tests failed, were flaky or did not execute.')
    p.require(helper.get('successful') is True and helper.get('passed',0)>0 and helper.get('failed',-1)==helper.get('errors',-1)==0,'Actual helper tests failed or did not execute.')
    smoke=p.read_json(recovery);p.require(smoke.get('result')=='passed','Recording recovery check did not pass.')
    value=dict(format_version=1,validation_mode='automated-alpha',version=p.RELEASE_VERSION,source_commit=commit,
               completed_utc=datetime.now(timezone.utc).isoformat(),hardware_acceptance='notVerified',github_publication='notPublished',
               checks=dict(dotnet={**dotnet,'report_sha256':digest(unit)},browser={**web,'report_sha256':digest(browser)},helpers={**helper,'report_sha256':digest(helpers)},recovery={**smoke,'report_sha256':digest(recovery)}))
    installer.write_json(output,value);return value

def validate_evidence(value,commit,payload=None):
    p.require(value.get('format_version')==1 and value.get('validation_mode')=='automated-alpha' and value.get('version')==p.RELEASE_VERSION
              and value.get('source_commit')==commit and value.get('hardware_acceptance')=='notVerified' and value.get('github_publication')=='notPublished','Automatic validation identity/source/hardware status differs.')
    p.validate_date(value.get('completed_utc'),'Automatic validation timestamp')
    checks=value.get('checks',{});net=checks.get('dotnet',{});web=checks.get('browser',{});helper=checks.get('helpers',{})
    p.require(net.get('passed',0)>0 and net.get('failed',-1)==net.get('error',-1)==net.get('aborted',-1)==0,'Required .NET evidence is not passing.')
    p.require(web.get('expected',0)>0 and web.get('unexpected',-1)==0 and web.get('flaky',0)==0,'Required browser evidence is not passing.')
    p.require(helper.get('successful') is True and helper.get('passed',0)>0 and helper.get('failed',-1)==helper.get('errors',-1)==0 and checks.get('recovery',{}).get('result')=='passed','Helper/recovery evidence is not passing.')
    for check in checks.values():p.require(isinstance(check.get('report_sha256'),str) and len(check['report_sha256'])==64 and all(c in '0123456789abcdef' for c in check['report_sha256']),'Automatic report checksum missing.')
    if payload is not None:p.require(value.get('payload_sha256')==payload,'Automatic validation payload differs.')
    p.require(p.identity()['prerelease'],'Automatic source-only finalization is for explicitly marked alpha/prerelease builds.')
    return value

def finalize(folder,output,evidence_path):
    receipt=installer.validate_candidate(folder);meta=p.identity();validation=validate_evidence(p.read_json(evidence_path),receipt['source_commit'])
    validation={**validation,'payload_sha256':receipt['payload_sha256']}
    output.mkdir(parents=True,exist_ok=True)
    names=(meta['setup_name'],meta['setup_name']+'.sha256',meta['setup_record_name'],meta['asset_name'],meta['asset_name']+'.sha256','RELEASE-NOTES.md')
    if any((output/name).exists() for name in names):
        p.require(all((output/name).exists() for name in names),'Incomplete/conflicting final output. Preserve files and use a separate output folder.')
        return verify(output/meta['setup_name'],output/meta['setup_record_name'],output/meta['asset_name'],receipt['source_commit'])
    with tempfile.TemporaryDirectory(prefix='aeroscience-auto-final-',dir=output) as temp:
        work=Path(temp);root=work/meta['root_name'];shutil.copytree(folder/'payload',root)
        (root/'ACCEPTANCE.json').unlink(missing_ok=True);(root/'RELEASE-STATUS.txt').unlink(missing_ok=True)
        installer.write_json(root/'ALPHA-VALIDATION.json',validation)
        p.require(p.validate_payload(root,final=True)==(receipt['source_commit'],receipt['payload_sha256']),'Final payload changed.')
        p.zip_tree(root,work/meta['asset_name']);p.write_checksum(work/meta['asset_name'])
        shutil.copy2(folder/meta['setup_name'],work/meta['setup_name']);p.write_checksum(work/meta['setup_name'])
        installer.write_json(work/meta['setup_record_name'],dict(format_version=2,build=receipt,validation=validation,installer_receipt_sha256=digest(folder/'build-receipt.json')))
        info=verify(work/meta['setup_name'],work/meta['setup_record_name'],work/meta['asset_name'],receipt['source_commit'])
        (work/'RELEASE-NOTES.md').write_text(p.release_notes(info),encoding='utf-8',newline='\n')
        for name in names:(work/name).rename(output/name)
    return info

def verify(setup,record_path,archive,expected_commit=None):
    info=p.verify_archive(archive,archive.with_name(archive.name+'.sha256'));record=p.read_json(record_path);build=record.get('build',{})
    p.require(record.get('format_version')==2 and record.get('validation')==info.get('validation'),'Automatic release record/archive evidence mismatch.')
    validate_evidence(record['validation'],info['source_commit'],info['payload_sha256']);installer.pe(setup)
    p.require(build.get('source_commit')==info['source_commit'] and build.get('payload_sha256')==info['payload_sha256'] and build.get('version')==p.RELEASE_VERSION,'Installer source/version/payload mismatch.')
    p.require(build.get('setup_name')==setup.name==p.identity()['setup_name'] and build.get('setup_sha256')==digest(setup),'Installer bytes differ from compiler receipt.')
    p.require(setup.with_name(setup.name+'.sha256').read_text().strip()==digest(setup)+'  '+setup.name,'Installer checksum mismatch.')
    p.require(hashlib.sha256((json.dumps(build,sort_keys=True,indent=2)+'\n').encode()).hexdigest()==record.get('installer_receipt_sha256'),'Installer build receipt fingerprint differs.')
    p.require(build.get('recipe_sha256')==installer.recipe_sha(),'Installer recipe changed; select the original source revision.')
    if expected_commit:p.require(expected_commit==info['source_commit'],'Changed source: existing finalized output belongs to another commit; nothing was overwritten.')
    with tempfile.TemporaryDirectory(prefix='aeroscience-auto-proof-') as temp:
        with zipfile.ZipFile(archive) as contents:contents.extractall(temp)
        root=Path(temp)/p.identity()['root_name']
        found={name:digest(path) for name,path in sorted(p.files(root).items()) if name!='ALPHA-VALIDATION.json'}
        p.require(found==build.get('files'),'Final archived payload differs from the Inno compiler receipt.')
    result={**info,'distribution':'installer','portable_asset_name':info['asset_name'],'asset_name':setup.name,'sha256':digest(setup),'hardware_acceptance':'notVerified','github_publication':'notPublished'}
    notes=setup.parent/'RELEASE-NOTES.md'
    if notes.exists():p.require(notes.read_text(encoding='utf-8')==p.release_notes(result),'Release notes differ from verified alpha metadata.')
    return result

def status(output,commit):
    meta=p.identity();setup=output/meta['setup_name'];record=output/meta['setup_record_name'];archive=output/meta['asset_name']
    if any(path.exists() for path in (setup,record,archive)):
        if not all(path.exists() for path in (setup,record,archive,output/'RELEASE-NOTES.md',setup.with_name(setup.name+'.sha256'),archive.with_name(archive.name+'.sha256'))):return dict(state='incomplete',message='Partial final output exists. Preserve it and choose another output directory.')
        try:return dict(state='finalized',info=verify(setup,record,archive,commit))
        except (ValueError,OSError,KeyError,json.JSONDecodeError) as error:return dict(state='changedSource' if 'Changed source' in str(error) else 'corruptOrConflicting',message=str(error))
    if (output/'installer-candidate').exists() or (output/'release-candidate').exists():return dict(state='incomplete',message='An incomplete candidate exists; preserve it and choose another output directory. No existing artifact was overwritten.')
    if list(output.glob('AeroScience-Setup-v*.exe')):return dict(state='conflictingVersion',message='Another version exists in this output directory; choose a separate versioned folder.')
    return dict(state='empty')

def main():
    parser=argparse.ArgumentParser(description=__doc__);sub=parser.add_subparsers(dest='command',required=True)
    record=sub.add_parser('evidence')
    for key in ('unit','browser','helpers','recovery','output'):record.add_argument('--'+key,type=Path,required=True)
    record.add_argument('--source-commit',required=True)
    final=sub.add_parser('finalize')
    for key in ('folder','output','evidence'):final.add_argument('--'+key,type=Path,required=True)
    state=sub.add_parser('status');state.add_argument('--output',type=Path,required=True);state.add_argument('--source-commit',required=True)
    args=parser.parse_args()
    result=evidence(args.unit,args.browser,args.helpers,args.recovery,args.source_commit,args.output) if args.command=='evidence' else finalize(args.folder,args.output,args.evidence) if args.command=='finalize' else status(args.output,args.source_commit)
    print(json.dumps(result))

if __name__=='__main__':
    try:main()
    except (ValueError,OSError,KeyError,json.JSONDecodeError,xml.ParseError) as error:raise SystemExit('Automatic release validation refused: '+str(error))
