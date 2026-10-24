"""Run genuine bounded acceptance, independent checks and verified recorders on frozen source.

No fixtures, owner credentials, installer finalization or publication are performed.
"""
import argparse
import json
from pathlib import Path
import shutil
import subprocess
import sys
import verify_scientific_acceptance as verification
from scientific_evidence import begin,seal,digest,refuse_secrets
import record_envi_acceptance
import record_workspace_acceptance
import record_scientific_acceptance


def complete(output,python,originals=None):
    output=Path(output).resolve()
    if output.exists():raise ValueError('Preserve existing evidence; choose a new nonexistent output directory.')
    output.mkdir(parents=True)
    root=verification.ROOT
    def command(args,name):
        with (output/(name+'.log')).open('a',encoding='utf-8') as log:
            result=subprocess.run([str(a) for a in args],cwd=root,stdout=log,stderr=subprocess.STDOUT)
        if result.returncode:raise RuntimeError(name+' failed (exit '+str(result.returncode)+'); preserve its log and original data.')
    command(['dotnet','restore','AeroScience.sln','--locked-mode'],'locked-restore')
    # Importing unchanged, independently hash-identified original files is reuse of
    # real data, not reuse of an old source/software acceptance result.
    if originals is None:
        originals=output/'cuprite-originals'
        command([python,'scripts/research/download_cuprite_acceptance.py','--output',originals],'cuprite-download')
    originals=Path(originals).resolve()
    receipt=json.loads((originals/'download-receipt.json').read_text())
    if receipt.get('url')!='https://vis-webcontent.s3.amazonaws.com/tutorials/hyperspectral.zip' or receipt.get('networkBytes',0)<=0:
        raise ValueError('Genuine Cuprite download receipt is required.')
    for name,(expected,size) in record_envi_acceptance.EXPECTED.items():
        p=originals/name
        if p.is_symlink() or p.stat().st_size!=size or digest(p)!=expected:raise ValueError('Pinned genuine Cuprite originals changed.')
    statuses={}
    def workload(name,args,comparisons):
        folder=output/name;source=begin(folder,name)
        try:
            command(['dotnet','run','--project','tests/AeroScience.ProviderAcceptance','-c','Release','--no-restore','--','--network',*args,'--output',folder],name)
            if name=='envi':shutil.copy2(originals/'download-receipt.json',folder/'download-receipt.json')
            for tool,options in comparisons:
                command([python,tool,*[str(v).replace('{folder}',str(folder)) for v in options]],name+'-independent')
            seal(folder,source);statuses[name]={'status':'passed'}
            print(name+': genuine execution and verification passed.',flush=True)
        except (RuntimeError,ValueError,OSError) as error:
            statuses[name]={'status':'failed','error':str(error)}
            print(name+': '+str(error),flush=True)
        return folder
    providers=workload('providers',['--workspace','--regional-mosaic'],[
        ('scripts/research/verify_library_sources.py',['{folder}','--providers']),
        ('scripts/research/verify_workspace_rasters.py',['--folder','{folder}','--output','{folder}/independent-workspace-comparison.json'])])
    envi=workload('envi',['--envi',str(originals/'CupriteReflectance.hdr'),'--workspace'],[
        ('scripts/research/verify_envi_originals.py',['--folder','{folder}']),
        ('scripts/research/verify_workspace_rasters.py',['--folder','{folder}','--output','{folder}/independent-workspace-comparison.json'])])
    library=workload('library',['--library'],[('scripts/research/verify_library_sources.py',['{folder}'])])
    nasa=workload('nasa-metadata',['--nasa'],[])
    # Recording cannot turn an incomplete raw run into a pass. Each recorder checks
    # captured source and sealed input hashes, then validates before atomic replacement.
    try:
        if statuses['envi']['status']=='passed':record_envi_acceptance.record(envi,root/f'docs/research/v{verification.VERSION}-guided-envi-acceptance.json')
        if all(statuses[n]['status']=='passed' for n in ('providers','envi')):
            record_workspace_acceptance.record(providers,envi,root/f'docs/research/v{verification.VERSION}-workspace-acceptance.json')
        if statuses['providers']['status']=='passed':record_scientific_acceptance.record(providers,verification.REPORT)
        verification.verify()
        statuses['release-scientific-gates']={'status':'passed'}
    except (OSError,ValueError,KeyError) as error:
        statuses['release-scientific-gates']={'status':'failed','error':str(error)}
    statuses['nasa-originals']={'status':'pending-owner-authorization','classic':'not tested','ng':'not tested','emit':'experimental application integration; authorized original numerical acceptance pending' if verification.VERSION=='0.6.0' else 'discovery only; native import unsupported'}
    refuse_secrets(statuses)
    (output/'summary.json').write_text(json.dumps(statuses,indent=2)+'\n')
    if any(v['status']=='failed' for v in statuses.values()):raise ValueError('One or more genuine workflows failed. No failed result was promoted; see summary/logs.')
    print('Public provider, workspace and guided ENVI gates passed. Protected NASA originals and installed Windows/MSFS/device acceptance remain pending.')
    return statuses


def complete_nasa_original(output,python,originals,family):
    """Local original sampling only; does not acquire credentials or assert access."""
    output=Path(output).resolve()
    if output.exists():raise ValueError('Choose a new owner evidence directory.')
    output.mkdir(parents=True);folder=output/family;source=begin(folder,'nasa-original-'+family)
    commands=[['dotnet','run','--project','tests/AeroScience.ProviderAcceptance','-c','Release','--','--nasa-retained-source',str(originals),'--nasa-family',family,'--output',str(folder)],
              [python,'scripts/research/verify_nasa_originals.py','--folder',str(folder),'--originals',str(originals)]]
    with (output/'local-original-verification.log').open('w',encoding='utf-8') as log:
        for command in commands:
            subprocess.run(command,cwd=verification.ROOT,stdout=log,stderr=subprocess.STDOUT,check=True)
    seal(folder,source)
    print('Local '+family+' original numerical comparison passed. This does not establish authorized acquisition or installed/hardware acceptance. Keep raw originals/spectra private; review Group J evidence separately.')


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output',type=Path,required=True)
    parser.add_argument('--python',default=sys.executable,help='Developer Python with rasterio/numpy; never a shipped runtime dependency.')
    parser.add_argument('--cuprite-originals',type=Path,help='Existing genuine pinned originals plus their actual download receipt; otherwise bounded download is performed.')
    parser.add_argument('--nasa-retained-source',type=Path,help='Owner-local retained source folder, already acquired through the official authorized workflow; no download/login is performed.')
    parser.add_argument('--nasa-family',choices=['classic','ng'])
    args=parser.parse_args()
    try:
        if args.nasa_retained_source:
            if not args.nasa_family:parser.error('--nasa-family is required for local NASA originals.')
            complete_nasa_original(args.output,args.python,args.nasa_retained_source,args.nasa_family)
        else:complete(args.output,args.python,args.cuprite_originals)
    except (OSError,ValueError,RuntimeError,subprocess.CalledProcessError) as error:raise SystemExit('Scientific acceptance incomplete: '+str(error))
