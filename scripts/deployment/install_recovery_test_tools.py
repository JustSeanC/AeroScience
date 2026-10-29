#!/usr/bin/env python3
"""Install pinned disposable recovery tools for Linux CI. No provider, credentials or paid infrastructure."""
import bz2
import hashlib
import os
from pathlib import Path
import shutil
import subprocess
import urllib.request

def install(root):
    root=Path(root);root.mkdir(parents=True,exist_ok=True)
    url='https://github.com/restic/restic/releases/download/v0.19.1/restic_0.19.1_linux_amd64.bz2'
    with urllib.request.urlopen(url,timeout=60) as response:compressed=response.read(32*1024*1024+1)
    if hashlib.sha256(compressed).hexdigest()!='f415415624dcc452f2a02b8c33641791a8c6d6d3b65bbb3543fcf9a25151585c':raise RuntimeError('Pinned recovery tool hash failed')
    target=root/'restic';target.write_bytes(bz2.decompress(compressed));target.chmod(0o755)
    # Reuse the exact PostgreSQL service container's supported client binaries. No mutable apt client or server downgrade.
    containers=subprocess.check_output(['docker','ps','--filter','ancestor=postgres:17.6-bookworm@sha256:f3bd19c606e442c3d7bdfa8002e03fe260a1023351e0ea4598032022b68dd6e3','--format','{{.ID}}'],text=True).split()
    if len(containers)!=1:raise RuntimeError('Expected the isolated pinned PostgreSQL fixture')
    values={'AEROSCIENCE_RECOVERY_RESTIC':str(target.resolve())}
    for name in ('pg_dump','pg_restore'):
        destination=root/name;subprocess.run(['docker','cp',containers[0]+':/usr/lib/postgresql/17/bin/'+name,str(destination)],check=True,stdout=subprocess.DEVNULL);destination.chmod(0o755);values['AEROSCIENCE_RECOVERY_'+name.upper()]=str(destination.resolve())
    libraries=root/'lib';libraries.mkdir(exist_ok=True)
    dependencies=subprocess.check_output(['docker','exec',containers[0],'ldd','/usr/lib/postgresql/17/bin/pg_dump','/usr/lib/postgresql/17/bin/pg_restore'],text=True)
    for line in set(dependencies.splitlines()):
        parts=line.split()
        if len(parts)>2 and parts[1]=='=>' and parts[2].startswith('/') and not parts[0].startswith(('libc.so','libm.so','ld-linux')):
            subprocess.run(['docker','cp','-L',containers[0]+':'+parts[2],str(libraries/Path(parts[2]).name)],check=True,stdout=subprocess.DEVNULL)
    values['AEROSCIENCE_RECOVERY_PG_LIBRARY_PATH']=str(libraries.resolve())
    with open(os.environ['GITHUB_ENV'],'a') as output:
        for name,value in values.items():output.write(name+'='+value+'\n')
if __name__=='__main__':install(os.environ['RUNNER_TEMP']+'/aeroscience-recovery-tools')
