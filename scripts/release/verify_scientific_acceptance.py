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
 'src/AeroScience.Missions/ScientificTransferLedger.cs',
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
if tuple(map(int, VERSION.split('-')[0].split('.'))) >= (0, 4, 0):
    SOURCES += [
        'src/AeroScience.Shared/MissionScienceModels.cs', 'src/AeroScience.Shared/Models.cs', 'src/AeroScience.Shared/ResearchModels.cs',
        'src/AeroScience.Missions/InstrumentProfiles.cs', 'src/AeroScience.Missions/ScientificCapabilities.cs',
        'src/AeroScience.Missions/SensorGeometry.cs', 'src/AeroScience.Missions/TerrainPlanning.cs',
        'src/AeroScience.Missions/ScientificCoverage.cs', 'src/AeroScience.Missions/ScientificComposites.cs',
        'src/AeroScience.Missions/ScientificSampleIndex.cs', 'src/AeroScience.Missions/ScientificArtifacts.cs',
        'src/AeroScience.Missions/ScientificGeoTiff.cs', 'src/AeroScience.Missions/ScientificArtifactPreview.cs',
        'src/AeroScience.Missions/ScientificCache.cs', 'src/AeroScience.Missions/ResearchPackage.cs',
        'src/AeroScience.Missions/ResearchOperations.cs', 'src/AeroScience.Missions/MissionCoordinator.cs',
        'src/AeroScience.Missions/AtmosphericAnalysis.cs', 'src/AeroScience.Missions/EnvironmentTelemetryCache.cs',
        'tests/AeroScience.ProviderAcceptance/WorkspaceAcceptance.cs', 'tests/AeroScience.ProviderAcceptance/RegionalWorkspaceAcceptance.cs',
        'scripts/research/verify_workspace_rasters.py', 'scripts/research/verify_envi_originals.py',
    ]

if tuple(map(int, VERSION.split('-')[0].split('.'))) >= (0,4,1):
    SOURCES += ['src/AeroScience.Missions/ScienceStore.cs','src/AeroScience.Missions/MissionStore.cs','src/AeroScience.Missions/MissionReplay.cs','src/AeroScience.Missions/FlightPatterns.cs','src/AeroScience.Missions/RouteExports.cs','src/AeroScience.Engine/ResearchApi.cs','src/AeroScience.Missions/ScientificDownlink.cs','src/AeroScience.Missions/ScientificWorkspace.cs',
                'tests/AeroScience.ProviderAcceptance/ReviewAcceptance.cs','src/AeroScience.Missions/ScientificStatisticsCache.cs','src/AeroScience.Missions/ScientificInterpretation.cs','src/AeroScience.Missions/ScientificAtmosphereMap.cs','src/AeroScience.Missions/TransectPasses.cs','src/AeroScience.Engine/ScientificDownlinkWorker.cs','src/AeroScience.Engine/ScientificDownlinkSignals.cs']

def source_digest(path):
    return hashlib.sha256(path.read_text(encoding='utf-8').replace('\r\n','\n').encode()).hexdigest()
def verify_guided_envi(report=None,root=ROOT):
    report=report or root/f'docs/research/v{VERSION}-guided-envi-acceptance.json'
    value=json.loads(report.read_text(encoding='utf-8'))
    if value.get('evidenceType')=='pending-genuine-guided-envi':raise ValueError('Current-source genuine guided ENVI acceptance is pending; no import/sampling pass is approved.')
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
    if value.get('evidenceType')=='pending-genuine-provider-network':
        raise ValueError('Current '+VERSION+' genuine online-provider acceptance is pending: '+str(value.get('finalNetworkRerunReason') or 'Run the bounded provider harness and record its actual results.'))
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
    if tuple(map(int,VERSION.split("-")[0].split("."))) >= (0,4,0): verify_workspace(root=root)
    return value
def verify_workspace(report=None,root=ROOT):
    path=report or root/f'docs/research/v{VERSION}-workspace-acceptance.json'
    value=json.loads(path.read_text(encoding='utf-8'))
    if value.get('evidenceType')=='pending-genuine-workspace':raise ValueError('Current-source genuine workspace acceptance is pending; no numeric-support pass is approved.')
    hashes=value.get('canonicalLfSourceSha256',{})
    if value.get('evidenceType')!='genuine-scientific-workspace' or value.get('windowsMsfs')!='not tested' or set(hashes)!=set(SOURCES) or any(source_digest(root/n)!=h for n,h in hashes.items()):
        raise ValueError('Current-source genuine workspace/composite evidence is missing or stale.')
    results=value.get('independentRasterResults',[])
    if len(results)<12 or any(r.get('nodataSupportMatches') is not True or r.get('nativeAffineAndCrsMatch') is not True or r.get('maximumAbsoluteDifference',1)>1e-9 for r in results):
        raise ValueError('Independent genuine raster value/grid/nodata comparisons are incomplete.')
    if value.get('regionalSourcesCombined',0)<2 or value.get('originalIntegrityVerified') is not True or value.get('researchPackagesVerified',0)<5:
        raise ValueError('Genuine regional combination, original integrity and research-package evidence are incomplete.')
    return value

if __name__=='__main__':
    try:
        value=verify();print('Genuine numeric provider acceptance passed for four workflows. Windows/MSFS acceptance remains separate.')
    except (OSError,ValueError,KeyError) as error:raise SystemExit('Scientific acceptance refused: '+str(error))
