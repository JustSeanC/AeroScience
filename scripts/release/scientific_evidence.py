"""Execution-time provenance and verified file access shared by genuine-run recorders."""
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path, PureWindowsPath
import subprocess
import uuid
import verify_scientific_acceptance as verification


def digest(path):
    with Path(path).open('rb') as stream:
        return hashlib.file_digest(stream,'sha256').hexdigest()


def safe_file(folder,relative):
    relative=Path(str(relative).replace('\\','/'))
    if relative.is_absolute() or PureWindowsPath(str(relative)).is_absolute() or '..' in relative.parts:
        raise ValueError('Evidence path must remain inside its run directory.')
    folder=Path(folder).resolve();path=folder/relative
    if not path.resolve().is_relative_to(folder) or any(p.is_symlink() for p in [path,*path.parents] if p.is_relative_to(folder)):
        raise ValueError('Evidence links or escaping paths are refused.')
    if not path.is_file():raise ValueError('Required genuine evidence file is missing: '+str(relative))
    return path


def begin(folder,workload):
    folder=Path(folder)
    if folder.exists():raise ValueError('Use a new genuine acceptance run directory.')
    folder.parent.mkdir(parents=True,exist_ok=True)
    dirty=subprocess.check_output(['git','status','--porcelain'],cwd=verification.ROOT,text=True)
    if dirty.strip():raise ValueError('Freeze and commit source before starting genuine acceptance.')
    value={'formatVersion':1,'runId':str(uuid.uuid4()),'workload':workload,'status':'running',
           'sourceCommit':subprocess.check_output(['git','rev-parse','HEAD'],cwd=verification.ROOT,text=True).strip(),
           'startedUtc':datetime.now(timezone.utc).isoformat(),'canonicalLfSourceSha256':verification.source_hashes()}
    (folder.parent/(folder.name+'-source-start.json')).write_text(json.dumps(value,indent=2)+'\n')
    return value


def seal(folder,value):
    folder=Path(folder)
    if value['canonicalLfSourceSha256']!=verification.source_hashes():raise ValueError('Source changed during scientific execution; rerun required.')
    if subprocess.check_output(['git','status','--porcelain'],cwd=verification.ROOT,text=True).strip():raise ValueError('Checkout changed during scientific execution; rerun required.')
    value={**value,'status':'passed','completedUtc':datetime.now(timezone.utc).isoformat(),
           'artifacts':{p.relative_to(folder).as_posix():digest(p) for p in folder.glob('*.json') if p.name!='run-source.json'}}
    (folder/'run-source.json').write_text(json.dumps(value,indent=2)+'\n')


def load_run(folder,workload):
    folder=Path(folder);path=safe_file(folder,'run-source.json');value=json.loads(path.read_text())
    if value.get('formatVersion')!=1 or value.get('status')!='passed' or value.get('workload')!=workload or not value.get('completedUtc'):
        raise ValueError('A completed genuine '+workload+' run is required, not an incomplete receipt.')
    if value.get('canonicalLfSourceSha256')!=verification.source_hashes():raise ValueError('Run executed different source files; do not restamp stale evidence.')
    for name,expected in value.get('artifacts',{}).items():
        if digest(safe_file(folder,name))!=expected:raise ValueError('Run artifact changed after scientific verification: '+name)
    if not value.get('artifacts'):raise ValueError('Completed run has no verified artifacts.')
    return value,dict(runId=value['runId'],status='passed',sourceCommit=value['sourceCommit'],
                      sourceManifestSha256=verification.manifest_digest(value['canonicalLfSourceSha256']),receiptSha256=digest(path))


def require_artifact(folder,run,name):
    if name not in run['artifacts']:raise ValueError('Evidence was not part of the completed run: '+name)
    return safe_file(folder,name)


def verify_source(folder,source):
    root=Path(folder)/'ScienceLibrary'
    for file in source['files']:
        path=safe_file(root,str(Path(source['relativeDirectory'])/file['relativePath']))
        if path.stat().st_size!=file['bytes'] or digest(path)!=file['sha256']:raise ValueError('Retained scientific original changed.')


def refuse_secrets(value):
    text=json.dumps(value).casefold()
    if any(key in text for key in ('sig=','access_token=','token=','x-amz-signature=','x-amz-credential=')):
        raise ValueError('Authorization-bearing URLs must not enter public evidence.')
