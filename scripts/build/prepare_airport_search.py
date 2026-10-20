"""Prepare a pinned offline airport search index from genuine OurAirports CSV.

Maintainer-only; installed pilots never run this or contact a geocoder.
"""
import argparse,csv,hashlib,io,json,math
from pathlib import Path
ROOT=Path(__file__).resolve().parents[2]
COMMIT='b529623d2a0b09a9be78d37fc7a7a5e1e16e3451'
def prepare(source):
    raw=source.read_bytes();rows=[]
    if hashlib.sha256(raw).hexdigest()!='0499dbd88de2fc735ac5c09f59796ccc3af544e17ffda807f793c0c6fe6c3d75':
        raise ValueError('CSV does not match the reviewed OurAirports commit; review the source snapshot before updating its pin.')
    for r in csv.DictReader(io.StringIO(raw.decode('utf-8-sig'))):
        if r['type']=='closed':continue
        try:lat=float(r['latitude_deg']);lon=float(r['longitude_deg'])
        except ValueError:continue
        if not(math.isfinite(lat) and math.isfinite(lon) and -90<=lat<=90 and -180<=lon<=180):continue
        rows.append([r['ident'],r.get('icao_code') or r['gps_code'],r['iata_code'],r['name'],r['municipality'],r['iso_region'],r['iso_country'],lat,lon,r['type']])
    rows.sort(key=lambda r:r[0])
    web=ROOT/'src/AeroScience.Engine/wwwroot';path=web/'geography/airport-search.json'
    value=dict(formatVersion=1,columns=['ident','icao','iata','name','city','region','country','latitude','longitude','type'],airports=rows,
               attribution='OurAirports public domain; community data, not certified navigation; https://ourairports.com/data/')
    path.write_text(json.dumps(value,ensure_ascii=False,separators=(',',':'))+'\n',encoding='utf-8',newline='\n')
    proof=dict(dataset='OurAirports',license='Public domain, no guarantee of accuracy or fitness for use',licenseUrl='https://ourairports.com/data/',sourceCommit=COMMIT,sourceUrl=f'https://raw.githubusercontent.com/davidmegginson/ourairports-data/{COMMIT}/airports.csv',sourceSha256=hashlib.sha256(raw).hexdigest(),features=len(rows),excluded='Closed airports and invalid coordinates only; original positions preserved',bundledSha256=hashlib.sha256(path.read_bytes()).hexdigest())
    provenance=web/'geography/airport-search-provenance.json';provenance.write_text(json.dumps(proof,indent=2)+'\n',encoding='utf-8',newline='\n')
    manifest=web/'assets-manifest.json';v=json.loads(manifest.read_text(encoding='utf-8'))
    for p in (path,provenance):v['files'][str(p.relative_to(web))]=hashlib.sha256(p.read_bytes()).hexdigest()
    manifest.write_text(json.dumps(v,indent=2)+'\n',encoding='utf-8',newline='\n');return proof
if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('--csv',type=Path,required=True);print(json.dumps(prepare(p.parse_args().csv)))
