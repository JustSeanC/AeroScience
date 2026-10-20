"""Record actual guided-import acceptance of the public, hash-identified AVIRIS Cuprite example."""
import argparse
import hashlib
import json
from pathlib import Path
import verify_scientific_acceptance as verification

# Retrieved from the public NV5 tutorial ZIP; original numeric/header files are not bundled.
EXPECTED = {
    'CupriteReflectance.dat': ('4cd54e7ef001bb7bcacde51712d2d1d69a338893403303f7d1d1e0da98eb1666', 140547456),
    'CupriteReflectance.hdr': ('095d7cd9d3221b15eca9da0050688de3480a1321d560925c02ac58e114f87d0b', 17526),
}
def record(folder,output):
    path=folder/'guided-envi-acceptance.json';value=json.loads(path.read_text(encoding='utf-8'))
    source=value['source'];inspection=value['inspection'];files={f['relativePath']:(f['sha256'],f['bytes']) for f in source['files']}
    if files!=EXPECTED or value.get('evidenceType')!='genuine-downloaded-envi' or value.get('flightInput')!='synthetic test position; not MSFS' or value.get('windowsMsfs')!='not tested':
        raise ValueError('Expected actual import/product acceptance of the hash-identified public Cuprite originals; mocks and hardware claims are refused.')
    validation=dict(automaticManifest=True,originalChannelsPreserved=True,originalFilesPreserved=value['externalOriginalsPreserved'],offlineReopen=value['offlineReopen'],duplicateReused=value['duplicateReused'],publishedProductSha256=value['publishedProduct']['sha256'],validSamples=value['validSamples'])
    comparison=json.loads((folder/'independent-envi-comparison.json').read_text(encoding='utf-8'))
    receipt=json.loads((folder/'download-receipt.json').read_text(encoding='utf-8'))
    if receipt.get('url')!='https://vis-webcontent.s3.amazonaws.com/tutorials/hyperspectral.zip' or receipt.get('networkBytes',0)<=0:raise ValueError('The actual public-data download receipt is required.')
    channels=[b['channel'] for b in source['manifest']['bands']]
    if set(channels)!=set(range(224)) or len(channels)!=224 or inspection['manifest'] is None or comparison.get('maximumAbsoluteDifference')!=0 or comparison.get('checkedGoodBands')!=170:raise ValueError('Original spectral channels or independent numeric checks are incomplete.')
    result=dict(formatVersion=1,version=verification.VERSION,evidenceType=value['evidenceType'],checkedUtc=value['checkedUtc'],windowsMsfs='not tested',firstTimeUser='not tested',flightInput=value['flightInput'],
        provider='NASA AVIRIS / NV5 Cuprite reflectance tutorial',downloadUrl='https://vis-webcontent.s3.amazonaws.com/tutorials/hyperspectral.zip',downloadArchiveBytes=302629552,
        dataset=source['manifest']['name'],sourceObservedUtc=source['manifest']['observationUtc'],sourceFiles=source['files'],bands=inspection['bandCount'],wavelengthMinimumNm=inspection['minimumWavelengthNm'],wavelengthMaximumNm=inspection['maximumWavelengthNm'],badBands=inspection['badBands'],unit=inspection['unit'],grid=inspection['grid'],coverage=inspection['coverage'],validation=validation,independentComparison=comparison,
        canonicalLfSourceSha256={name:verification.source_digest(verification.ROOT/name) for name in verification.SOURCES},
        notes='Actual public ENVI originals imported and sampled through production source/product pipeline. Original detector channels retained; 54 original bad bands excluded. Unknown acquisition date retained. No Windows/MSFS flight or uncoached-user acceptance asserted. Data files are not redistributed.',artifactSha256=hashlib.sha256(path.read_bytes()).hexdigest())
    # Download receipt belongs to the original acquisition, not each repeated local import.
    result['downloadedRangeBytes']=receipt['networkBytes']
    result['downloadReceiptSha256']=hashlib.sha256((folder/'download-receipt.json').read_bytes()).hexdigest()
    output.write_text(json.dumps(result,indent=2)+'\n',encoding='utf-8',newline='\n');verification.verify_guided_envi(output)
    return result
if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('--folder',type=Path,required=True);parser.add_argument('--output',type=Path,default=verification.ROOT/f'docs/research/v{verification.VERSION}-guided-envi-acceptance.json');args=parser.parse_args();record(args.folder,args.output);print('Recorded actual hash-identified Cuprite guided-import/product/offline evidence. Windows/MSFS remains untested.')
