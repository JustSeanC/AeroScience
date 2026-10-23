"""Bounded opt-in genuine FAA originals for the production chart reader's Windows checks.

Pinned identities; no substitution, general URL fetching or automatic application download.
"""
import argparse
import hashlib
import json
from pathlib import Path
import urllib.request
import zipfile

SOURCES = [
    ('https://aeronav.faa.gov/visual/09-03-2026/sectional-files/Hawaiian_Islands.zip',27291624,'351107a94c53634014410bacec43253ed4117a77f34a952207c7a7bd9112680e',['Hawaiian Islands SEC.tif']),
    ('https://aeronav.faa.gov/visual/09-03-2026/tac-files/Boston_TAC.zip',23963891,'961e6304c3b47c6e3d4e0722bb646f03fc363e10c930d4459c17e68818ba9ffa',['Boston TAC.tif']),
    ('https://aeronav.faa.gov/visual/09-03-2026/heli_files/Boston_Heli.zip',26153201,'ede35eade9ed8d625964ebd52e7d6556fd8d6dea8e68b42733c72e0c26a5a902',['Boston HEL.tif']),
    ('https://aeronav.faa.gov/enroute/09-03-2026/enr_l01.zip',8595043,'7e1e4d7053e445d5d2907cdd0f9d8a070a70b95e3eeeb82aa39187ec49858da7',['ENR_L01.tif']),
    ('https://aeronav.faa.gov/enroute/09-03-2026/enr_h01.zip',9180279,'f0f7025a93cb981688cd086afe3c70845cbba85acccff6bc658b683f4a2c6af4',['ENR_H01.tif'])]


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *args, **kwargs):
        raise ValueError('Changed FAA endpoint; no redirect/substitution is approved.')


def prepare(output):
    output.mkdir(parents=True,exist_ok=False)
    reference=json.loads((Path(__file__).parent/'chart-window-reference.json').read_text())
    expected={r['file']:r for r in reference['sources']}
    receipts=[]
    for index,(url,size,sha,names) in enumerate(SOURCES):
        archive=output/f'original-{index}.zip';total=0;digest=hashlib.sha256()
        request=urllib.request.Request(url,headers={'User-Agent':'AeroScience/0.6.0-chart-acceptance'})
        with urllib.request.build_opener(NoRedirect).open(request,timeout=60) as response,archive.open('xb') as target:
            if response.status!=200:raise ValueError('Require a complete original HTTP 200.')
            while data:=response.read(65536):
                total+=len(data)
                if total>size:raise ValueError('Pinned original exceeds its exact transfer bound.')
                digest.update(data);target.write(data)
        if total!=size or digest.hexdigest()!=sha:raise ValueError('Genuine original changed. Preserve it; never restamp.')
        with zipfile.ZipFile(archive) as source:
            if len(source.infolist())>256:raise ValueError('Bound the archive inventory.')
            for name in names:
                matches=[e for e in source.infolist() if e.filename==name]
                if len(matches)!=1 or not 1<=matches[0].file_size<=64*1024*1024 or (matches[0].external_attr>>16)&0xf000==0xa000:
                    raise ValueError('Unsupported exact original member size/type.')
                count=0;hash_file=hashlib.sha256()
                with source.open(matches[0]) as input_file,(output/name).open('xb') as target:
                    while data:=input_file.read(65536):
                        count+=len(data)
                        if count>matches[0].file_size:raise ValueError('Expanded original exceeded its bound.')
                        target.write(data);hash_file.update(data)
                if count!=matches[0].file_size or hash_file.hexdigest()!=expected[name]['sha256']:raise ValueError('Original TIFF identity differs.')
        receipts.append(dict(url=url,bytes=total,sha256=sha,files=names))
    (output/'acquisition.json').write_text(json.dumps(dict(evidence='Genuine public original acquisition only; not chart/neatline/owner acceptance.',archives=receipts),indent=2)+'\n')


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('--output',required=True,type=Path)
    prepare(parser.parse_args().output)
