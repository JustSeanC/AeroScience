"""Summarize an actual explicit provider-acceptance run; never generate pass flags from mocks."""
import argparse
from datetime import datetime,timezone
import hashlib
import json
from pathlib import Path
import verify_scientific_acceptance as verification

def record(folder,output,pending=False):
    results=[]
    for name in sorted(verification.WORKFLOWS):
        source=folder/(name+'-acceptance.json');value=json.loads(source.read_text(encoding='utf-8'))
        state=value['state'];catalog=value['source'];manifest=catalog['manifest'];product=value['publishedProduct']
        acquisition=manifest['acquisition']
        if not(state['status']=='Ready' and state['downloadedBytes']>0 and value['valid']>0 and product['sourceId']==catalog['id'] and product['sourceClass']=='externalDataset' and product['sha256'] and acquisition['unsignedAssets'] and 'SYNTHETIC' not in manifest['name'].upper()):
            raise ValueError('Incomplete genuine provider/product run: '+name)
        expected={'usgs-3dep':'usgs-3dep-numeric','landsat-c2-l2':'landsat-c2-l2','sentinel-2-l2a':'sentinel-2-l2a','cop-dem-glo-30':'cop-dem-glo-30'}[name]
        if (name=='usgs-3dep' and acquisition['workflow']!=expected) or (name!='usgs-3dep' and not manifest['name'].startswith(expected+' / ')):
            raise ValueError('Provider fallback is not evidence for the original named workflow: '+name)
        if any('sig=' in url.casefold() for url in acquisition['unsignedAssets']):raise ValueError('Signed authorization must not enter public source evidence.')
        results.append(dict(workflow=name,passed=True,provider=manifest['provider'],sceneId=manifest['sceneId'],sourceObservedUtc=manifest['observationUtc'],publicationUtc=manifest.get('publicationUtc'),
            nativeResolution=acquisition['nativeResolution'],verticalDatum=manifest['verticalDatum'],quality=acquisition['quality'],requestedArea=acquisition['requestedArea'],
            networkBytes=state['downloadedBytes'],retainedNumericBytes=sum(file['bytes'] for file in catalog['files']),bands=manifest['bands'],
            validDerivedSamples=value['valid'],sampleCount=len(value['locations']),publishedProductVerified=True,publishedProductSha256=product['sha256'],flightInput='synthetic test position; not MSFS',
            sourceFiles=catalog['files'],acceptanceArtifactSha256=hashlib.sha256(source.read_bytes()).hexdigest()))
    comparison=folder/'independent-numeric-comparison.json'
    value=dict(formatVersion=1,evidenceType='genuine-public-provider-network',checkedUtc=datetime.now(timezone.utc).isoformat(),windowsMsfs='not tested',
        description='Actual public API discovery, bounded numeric retrieval, internal manifest, verified library import, QA-aware sampling and persisted/reloaded mission products. Flight inputs are synthetic and explicitly labeled. Data files and SAS tokens are not bundled.',
        finalNetworkRerun='pending' if pending else 'passed',finalNetworkRerunReason='The final rerun was interrupted by the cloud HTTP tunnel returning 503 across providers. Earlier successful genuine-data runs are preserved below; this is not an acceptance pass for the final source.' if pending else None,
        canonicalLfSourceSha256={name:verification.source_digest(verification.ROOT/name) for name in verification.SOURCES},workflows=results,
        independentComparison=json.loads(comparison.read_text(encoding='utf-8')) if comparison.exists() else None,
        hyperspectral=dict(status='discovery only; automated preparation and NASA authorization unsupported',details=json.loads((folder/'hyperspectral-discovery.json').read_text(encoding='utf-8')) if (folder/'hyperspectral-discovery.json').exists() else None))
    output.parent.mkdir(parents=True,exist_ok=True);output.write_text(json.dumps(value,indent=2)+'\n',encoding='utf-8',newline='\n');verification.verify(output,require_current=not pending)
    return value
if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('--pending-final-rerun',action='store_true');parser.add_argument('--folder',type=Path,required=True);parser.add_argument('--output',type=Path,default=verification.REPORT);args=parser.parse_args()
    value=record(args.folder,args.output,args.pending_final_rerun);print('Recorded actual provider evidence for '+str(len(value['workflows']))+' numeric workflows; Windows/MSFS not tested.')
