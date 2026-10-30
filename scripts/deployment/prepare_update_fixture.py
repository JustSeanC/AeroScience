"""Build isolated old/new server binaries for a real paired-update test, never a release.

The old source is the accepted P28 baseline. The new binary uses this checkout with
a synthetic future informational version so normal signed-update policy is exercised.
No published image/digest, cloud deployment or production signing key is created.
"""
import argparse
import hashlib
from concurrent.futures import ThreadPoolExecutor
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import tarfile

ROOT = Path(__file__).resolve().parents[2]
BASELINE = '2b4c9abc82862fd7040cdd0f9e1a6f0b75a6dd49'
DIRECTORIES = ('src/AeroScience.Organization', 'src/AeroScience.Missions', 'src/AeroScience.Shared', 'src/AeroScience.Engine/wwwroot', 'licenses')

def run(arguments, log):
    result = subprocess.run(arguments, cwd=ROOT, capture_output=True, text=True, timeout=90)
    with log.open('a', encoding='utf-8') as stream:
        stream.write(result.stdout + result.stderr)
    if result.returncode:
        raise RuntimeError('Isolated update fixture build failed; retained build diagnostics contain no deployment credentials.')

def prepare(output):
    source = subprocess.run(['git', 'rev-parse', 'HEAD'], cwd=ROOT, capture_output=True, text=True, check=True).stdout.strip()
    if not re.fullmatch('[a-f0-9]{40}', source):
        raise ValueError('An exact current source identity is required.')
    output.mkdir(parents=True, exist_ok=True)
    marker=output/'.isolated-update-fixture'
    if not marker.exists() and any((output/name).exists() for name in ('old-source','new-source','old','new')):
        raise ValueError('Existing build children were not marked as a disposable update fixture.')
    marker.write_text('aeroscience-isolated-update-fixture-v1\n',encoding='utf-8')
    old, new = output/'old-source', output/'new-source'
    for path in (old, new, output/'old', output/'new'):
        if path.exists():
            shutil.rmtree(path)  # Fixed disposable build children only, never application data.
        path.mkdir()
    archive = output/'accepted-source.tar'
    run(['git', 'archive', '--format=tar', '--output='+str(archive), BASELINE, 'Directory.Build.props', 'global.json', *DIRECTORIES], output/'prepare.log')
    with tarfile.open(archive) as tar:
        for entry in tar.getmembers():
            if entry.issym() or entry.islnk() or Path(entry.name).is_absolute() or '..' in Path(entry.name).parts:
                raise ValueError('Unsupported trusted source archive layout.')
        tar.extractall(old, filter='data')
    archive.unlink()
    for name in DIRECTORIES:
        shutil.copytree(ROOT/name, new/name, ignore=shutil.ignore_patterns('bin', 'obj', 'node_modules', '__pycache__'))
    for name in ('Directory.Build.props', 'global.json'):
        shutil.copy2(ROOT/name, new/name)
    witness=hashlib.sha256()
    for path in sorted(p for p in new.rglob('*') if p.is_file()):
        witness.update(path.relative_to(new).as_posix().encode()+b'\0');witness.update(hashlib.sha256(path.read_bytes()).digest())
    dirty=bool(subprocess.run(['git','status','--porcelain','--',*DIRECTORIES,'Directory.Build.props','global.json'],cwd=ROOT,capture_output=True,text=True,check=True).stdout.strip())
    def build(pair):
        directory, version, revision, role = pair
        project = directory/'src/AeroScience.Organization/AeroScience.Organization.csproj'
        log = output/(role+'-build.log')
        run(['dotnet','restore',str(project),'--locked-mode','--nologo'], log)
        target = output/role
        run(['dotnet','publish',str(project),'-c','Release','--no-restore','-m:1','-nr:false','-p:UseSharedCompilation=false','-p:UseAppHost=false','-p:InformationalVersion='+version+'+'+revision,'-p:IncludeSourceRevisionInInformationalVersion=false','--nologo','-o',str(target)], log)
    with ThreadPoolExecutor(max_workers=2) as pool:
        list(pool.map(build, [(old,'0.7.0',BASELINE,'old'), (new,'0.7.1',source,'new')]))
    report={'format':'aeroscience-isolated-update-fixture','version':1,'oldSource':BASELINE,'newSource':source,'copiedSourceSha256':witness.hexdigest(),'workingTreeDirty':dirty,'newVersion':'0.7.1','syntheticFutureVersion':True,'productionDistribution':False,'registryIdentityVerified':False,'cloudCalls':0}
    (output/'fixture.json').write_text(json.dumps(report,indent=2)+'\n', encoding='utf-8')
    if os.environ.get('GITHUB_ENV'):
        with open(os.environ['GITHUB_ENV'],'a',encoding='utf-8') as env:
            env.write('AEROSCIENCE_UPDATE_FIXTURE='+str(output)+'\n')
    return report

if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('--output',type=Path,required=True);arguments=parser.parse_args()
    print(json.dumps(prepare(arguments.output.resolve())))
