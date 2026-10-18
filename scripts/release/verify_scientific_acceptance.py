"""Gate v0.3.0 automatic-science claims on recorded genuine-data acceptance.

This developer evidence is separate from Windows/MSFS acceptance. Hashing uses
canonical LF source text so a clean Windows Git checkout has the same identity.
"""
import hashlib
import json
from pathlib import Path

ROOT=Path(__file__).resolve().parents[2]
REPORT=ROOT/'docs/research/v0.3.0-automatic-provider-acceptance.json'
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
]
def source_digest(path):
    return hashlib.sha256(path.read_text(encoding='utf-8').replace('\r\n','\n').encode()).hexdigest()
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
        raise ValueError('Final automatic-provider network rerun is pending: '+value.get('finalNetworkRerunReason','See genuine provider report.'))
    return value
if __name__=='__main__':
    try:
        value=verify();print('Genuine numeric provider acceptance passed for four workflows. Windows/MSFS acceptance remains separate.')
    except (OSError,ValueError,KeyError) as error:raise SystemExit('Scientific acceptance refused: '+str(error))
