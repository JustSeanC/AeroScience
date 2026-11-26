"""Real schema-13 engine refusal against disposable schema-14 data. No installation."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import tarfile
import io

ROOT=Path(__file__).resolve().parents[2]
OLD_SOURCE='741e4a9de3484d24fb8e5886be8e3762d996ce84'

def run(args,log,cwd=ROOT,timeout=240):
    result=subprocess.run(list(map(str,args)),cwd=cwd,capture_output=True,text=True,timeout=timeout)
    log.write_text(result.stdout+result.stderr,encoding='utf-8')
    if result.returncode:raise RuntimeError('Fixture command failed; preserve '+str(log))
    return result

def probe(output,host,dotnet='dotnet'):
    output=Path(output).absolute();host=Path(host).absolute()
    if output.exists():raise ValueError('Use a fresh legacy-runtime receipt folder.')
    output.mkdir(parents=True)
    archive=subprocess.check_output(['git','archive',OLD_SOURCE],cwd=ROOT)
    source=output/'old-source';source.mkdir()
    with tarfile.open(fileobj=io.BytesIO(archive)) as tar:tar.extractall(source,filter='data')
    text=(source/'src/AeroScience.Missions/MissionStore.cs').read_text()
    if not re.search(r'CurrentSchemaVersion\s*=\s*13\s*;',text):raise ValueError('Pinned prior engine is not the actual schema-13 implementation.')
    artifacts=output/'old-artifacts'
    props=['-p:SourceRevisionId='+OLD_SOURCE,'-p:LidarBuildDirectory='+str(output/'old-native')]
    run([dotnet,'restore',source/'AeroScience.sln','--locked-mode','--artifacts-path',artifacts,*props],output/'old-restore.log',source)
    run([dotnet,'build',source/'src/AeroScience.Engine','-c','Release','--no-restore','--artifacts-path',artifacts,'-m:1','-nr:false','-p:UseSharedCompilation=false',*props],output/'old-build.log',source)
    engines=list((artifacts/'bin/AeroScience.Engine').rglob('AeroScience.Engine.dll'))
    if len(engines)!=1:raise ValueError('Ambiguous compiled prior engine.')
    data=output/'disposable-data'
    run([dotnet,host,'--p05-persistence-fixture',data],output/'fixture-create.log')
    fixture=json.loads((data/'fixture.json').read_text())
    args=[dotnet,engines[0],'--AeroScience:Telemetry=SimConnect','--AeroScience:RequireExistingDatabase=true',
        '--AeroScience:ConfigurationDirectory='+fixture['configuration'],'--AeroScience:DataDirectory='+fixture['missionDirectory'],
        '--AeroScience:ScienceLibraryDirectory='+fixture['scienceDirectory'],'--AeroScience:Port=5198']
    child=subprocess.Popen(list(map(str,args)),cwd=source,stdout=subprocess.PIPE,stderr=subprocess.STDOUT,text=True)
    try:log,_=child.communicate(timeout=30)
    except subprocess.TimeoutExpired:
        child.kill();log,_=child.communicate();(output/'old-refusal.log').write_text(log);raise RuntimeError('Prior engine did not refuse promptly; only the fixture child was stopped.')
    (output/'old-refusal.log').write_text(log,encoding='utf-8')
    if child.returncode==0 or 'Database was created by a newer AeroScience version; refusing to modify it.' not in log:raise ValueError('Expected actual prior-engine future-schema refusal; preserve diagnostics.')
    run([dotnet,host,'--p05-persistence-verify',data],output/'fixture-verify.log')
    receipt=dict(format='aeroscience-p05-prior-runtime',version=1,old_source=OLD_SOURCE,old_schema=13,new_schema=14,
        old_engine_sha256=hashlib.sha256(engines[0].read_bytes()).hexdigest(),old_exit_code=child.returncode,full_fingerprint_unchanged=True,
        scientific_identity_preserved=True,fixture='Synthetic disposable research only',installed_acceptance='Not Tested')
    (output/'receipt.json').write_text(json.dumps(receipt,indent=2)+'\n');print(json.dumps(receipt))
if __name__=='__main__':
    p=argparse.ArgumentParser();p.add_argument('--output',type=Path,required=True);p.add_argument('--host',type=Path,required=True);p.add_argument('--dotnet',default='dotnet');a=p.parse_args();probe(a.output,a.host,a.dotnet)
