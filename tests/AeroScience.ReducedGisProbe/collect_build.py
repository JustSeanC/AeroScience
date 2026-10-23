"""Retain bounded, non-secret native target CRT settings even when the import gate fails."""
import argparse
import json
from pathlib import Path

parser=argparse.ArgumentParser(description=__doc__)
parser.add_argument('--root',type=Path,required=True)
parser.add_argument('--output',type=Path,required=True)
args=parser.parse_args()
rows=[]
for path in sorted(args.root.rglob('*')):
    if not path.is_file() or path.is_symlink() or path.name not in ('CMakeCache.txt','build.ninja') or path.stat().st_size>33554432: continue
    settings=[]
    for line in path.read_text(encoding='utf-8',errors='replace').splitlines():
        if any(k in line for k in ('CMAKE_MSVC_RUNTIME_LIBRARY','CMAKE_C_FLAGS','CMAKE_CXX_FLAGS','FLAGS =','LINK_FLAGS =','/MT','/MD')):
            if len(line)>8192: raise ValueError('Unexpected native compiler setting size.')
            settings.append(line)
    if len(settings)>10000: raise ValueError('Unexpected native compiler inventory size.')
    rows.append({'file':path.relative_to(args.root).as_posix(),'settings':sorted(set(settings))})
    if len(rows)>256: raise ValueError('Unexpected native build inventory size.')
args.output.parent.mkdir(parents=True,exist_ok=True)
args.output.write_text(json.dumps({'evidenceClass':'Actual compiler configuration, not deployed runtime acceptance','files':rows},indent=2)+'\n')
