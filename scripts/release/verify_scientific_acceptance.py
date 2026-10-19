"""Gate automatic-science release claims on recorded genuine-data acceptance.

This developer evidence is separate from Windows/MSFS acceptance. Hashing uses
canonical LF source text so a clean Windows Git checkout has the same identity.
"""
import hashlib
import json
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT=Path(__file__).resolve().parents[2]
VERSION=ET.parse(ROOT/'Directory.Build.props').getroot().findtext('.//Version')
REPORT=ROOT/f'docs/research/v{VERSION}-automatic-provider-acceptance.json'
WORKFLOWS={'usgs-3dep','landsat-c2-l2','sentinel-2-l2a','cop-dem-glo-30'}
SOURCES=[
 'src/AeroScience.Missions/AutomaticScientificData.cs',
 'src/AeroScience.Missions/ScientificHttp.cs',
 'src/AeroScience.Missions/ScientificProviders.cs',
 'src/AeroScience.Missions/ScientificRaster.cs',
 'src/AeroScience.Missions/ScientificSourceSelection.cs',
 'src/AeroScience.Missions/RemoteScienceStore.cs',
 'src/AeroScience.Missions/RemoteAcquisition.cs',
 'src/AeroScience.Shared/RemoteSensingModels.cs',
 'src/AeroScience.Shared/ScientificAccessModels.cs',
 'tests/AeroScience.ProviderAcceptance/Program.cs',
 'tests/AeroScience.ProviderAcceptance/GuidedEnviAcceptance.cs',
 'src/AeroScience.Missions/HyperspectralImport.cs',
 'src/AeroScience.Missions/GuidedScientificImport.cs',
 'src/AeroScience.Missions/ScientificPrediction.cs',
 'src/AeroScience.Engine/ScientificPreparationWorker.cs',
]
def source_digest(path):
    return hashlib.sha256(path.read_text(encoding='utf-8').replace('\r\n','\n').encode()).hexdigest()
def verify_guided_envi(report=None,root=ROOT):
    report=report or root/f'docs/research/v{VERSION}-guided-envi-acceptance.json'
    value=json.loads(report.read_text(encoding='utf-8'))
    if value.get('evidenceType')!='genuine-downloaded-envi' or value.get('windowsMsfs')!='not tested' or value.get('flightInput')!='synthetic test position; not MSFS' or 'SYNTHETIC' in value.get('dataset','').upper():
        raise ValueError('Genuine guided ENVI evidence is missing or mislabeled; no hardware acceptance can be inferred.')
    hashes=value.get('canonicalLfSourceSha256',{})
    if set(hashes)!=set(SOURCES) or any(source_digest(root/name)!=sha for name,sha in hashes.items()):
        raise ValueError('Guided ENVI source changed since genuine import/product acceptance.')
    validation=value.get('validation',{})
    if value.get('bands',0)<16 or value.get('downloadedRangeBytes',0)<=0 or validation.get('validSamples',0)<=0 or len(validation.get('publishedProductSha256',''))!=64 or any(validation.get(k) is not True for k in ('automaticManifest','originalChannelsPreserved','originalFilesPreserved','offlineReopen','duplicateReused')):
        raise ValueError('Genuine guided ENVI import, retained originals, product and offline evidence are incomplete.')
    return value
def verify(report=REPORT,root=ROOT,require_current=True):
    value=json.loads(report.read_text(encoding='utf-8'))
    if value.get('formatVersion')!=1 or value.get('evidenceType')!='genuine-public-provider-network' or value.get('windowsMsfs')!='not tested':
        raise ValueError('Genuine provider acceptance identity/status is invalid; no Windows/MSFS pass may be inferred.')
    hashes=value.get('canonicalLfSourceSha256',{})
    if set(hashes)!=set(SOURCES) or any(source_digest(root/name)!=sha for name,sha in hashes.items()):
        raise ValueError('Automatic scientific source changed since genuine acceptance. Re-run the bounded network acceptance and update its factual report before distributing these claims.')
    results=value.get('workflows',[])
    if {r.get('workflow') for r in results}!=WORKFLOWS or len(results)!=len(WORKFLOWS):
        raise ValueError('Genuine acceptance must include USGS, Copernicus DEM, Landsat and Sentinel workflows.')
    for r in results:
        if not(r.get('passed') is True and r.get('networkBytes',0)>0 and r.get('validDerivedSamples',0)>0 and r.get('publishedProductVerified') is True and len(r.get('publishedProductSha256',''))==64 and r.get('sceneId') and r.get('bands') and r.get('flightInput')=='synthetic test position; not MSFS'):
            raise ValueError('Missing genuine discovery/retrieval/preparation/import/sampling/product evidence for '+str(r.get('workflow')))
    if require_current and value.get('finalNetworkRerun')!='passed':
        raise ValueError('Final automatic-provider network rerun is pending: '+(value.get('finalNetworkRerunReason') or 'See genuine provider report.'))
    verify_guided_envi(root=root)
    return value
if __name__=='__main__':
    try:
        value=verify();print('Genuine numeric provider acceptance passed for four workflows. Windows/MSFS acceptance remains separate.')
    except (OSError,ValueError,KeyError) as error:raise SystemExit('Scientific acceptance refused: '+str(error))
