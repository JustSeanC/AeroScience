"""Synthetic NetCDF-C originals based on pinned NASA writers, never NASA observations.
No network access. Refuse existing outputs; original byte hashes are retained.
"""
import ctypes as C
import ctypes.util
import argparse
import hashlib
import json
from pathlib import Path

ROOT=Path(__file__).resolve().parent
n=C.CDLL(ctypes.util.find_library('netcdf'))
def call(name,*args):
    result=getattr(n,name)(*args)
    if result:raise ValueError(name+': '+str(result))
def dim(g,name,length):
    value=C.c_int();call('nc_def_dim',g,name.encode(),C.c_size_t(length),C.byref(value));return value.value
def group(g,name):
    value=C.c_int();call('nc_def_grp',g,name.encode(),C.byref(value));return value.value
def text(g,v,key,value,string=False):
    data=value.encode()
    if string:call('nc_put_att_string',g,v,key.encode(),C.c_size_t(1),(C.c_char_p*1)(data))
    else:call('nc_put_att_text',g,v,key.encode(),C.c_size_t(len(data)),data)
def number(g,v,key,value,kind=5):
    ctype={5:C.c_float,6:C.c_double,4:C.c_int,7:C.c_ubyte}[kind]
    data=(ctype*1)(value);call({5:'nc_put_att_float',6:'nc_put_att_double',4:'nc_put_att_int',7:'nc_put_att_uchar'}[kind],g,v,key.encode(),kind,C.c_size_t(1),data)
def var(g,name,kind,dims,unit=None,fill=None):
    value=C.c_int();call('nc_def_var',g,name.encode(),kind,len(dims),(C.c_int*len(dims))(*dims),C.byref(value))
    if unit is not None:text(g,value.value,'units',unit)
    if fill is not None:number(g,value.value,'_FillValue',fill,kind)
    return value.value
def write(g,v,kind,data):
    ctype,fn={5:(C.c_float,'nc_put_var_float'),6:(C.c_double,'nc_put_var_double'),4:(C.c_int,'nc_put_var_int'),7:(C.c_ubyte,'nc_put_var_uchar'),12:(C.c_char_p,'nc_put_var_string')}[kind]
    call(fn,g,v,(ctype*len(data))(*data))

LABELS=['Cloud Flag','Cirrus Flag','Water Flag','Dilated Cloud Flag','SpecTf-Cloud Probability','SpecTf-Cloud Flag','SpecTf-Buffer Distance']
WKT='GEOGCS["WGS 84",DATUM["WGS_1984",SPHEROID["WGS 84",6378137,298.257223563,AUTHORITY["EPSG","7030"]],AUTHORITY["EPSG","6326"]],PRIMEM["Greenwich",0,AUTHORITY["EPSG","8901"]],UNIT["degree",0.0174532925199433,AUTHORITY["EPSG","9122"]],AUTHORITY["EPSG","4326"]]'
results=[]
variants=[('standard',['RFL','RFLUNCERT','MASK']),('scaled',['RFL','RFLUNCERT','MASK']),('shifted',['RFLUNCERT']),('spectral',['RFLUNCERT']),('time',['RFLUNCERT']),('glt',['MASK']),('old-mask',['MASK']),('labels',['MASK']),('version',['MASK']),('missing-units',['RFL']),('extremes',['RFL','RFLUNCERT','MASK']),('limits',['RFL','RFLUNCERT','MASK']),('oversized-string',['MASK']),('missing-packed',['MASK']),('producer',['RFL','RFLUNCERT','MASK']),('processing',['RFLUNCERT'])]
parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('--only',nargs='+',choices=[v[0] for v in variants]);parser.add_argument('--output',type=Path);args=parser.parse_args()
if args.output:ROOT=args.output.resolve();ROOT.mkdir(parents=True,exist_ok=False)
for variant,roles in variants:
    if args.only and variant not in args.only:continue
    for role in roles:
        version='003' if role=='MASK' else '002'
        path=ROOT/variant/f'EMIT_L2A_{role}_{version}_20990101T000000.nc'
        if path.exists():raise ValueError('Preserve existing fixture: '+str(path))
        path.parent.mkdir(exist_ok=True);handle=C.c_int();call('nc_create',str(path).encode(),0x1000,C.byref(handle));g=handle.value
        try:
            text(g,-1,'test_origin','SYNTHETIC NETCDF-C; NOT NASA SCIENCE OR AUTHENTIC ORIGINAL ACCEPTANCE')
            text(g,-1,'software_build_version','synthetic-different' if variant=='processing' else 'synthetic-mask-3' if role=='MASK' else 'synthetic-reflectance-2');text(g,-1,'software_delivery_version','synthetic-mask-delivery' if role=='MASK' else 'synthetic-reflectance-delivery')
            for key,value in {'instrument':'EMIT','flight_line':'emit20990101t000000','Conventions':'CF-1.13','product_version':'V002' if variant=='version' else 'V'+version,'time_coverage_start':'2099-01-01T00:00:01Z' if variant=='time' else '2099-01-01T00:00:00Z','time_coverage_end':'2099-01-01T00:00:12Z','title':{'RFL':'EMIT L2A Estimated Surface Reflectance 60 m V002','RFLUNCERT':'EMIT L2A Estimated Surface Reflectance Uncertainty 60 m V002','MASK':'EMIT L2A Masks 60 m V003'}[role],'spatial_ref':WKT}.items():text(g,-1,key,value,string=key=='Conventions')
            if variant=='producer':text(g,-1,'time_coverage_start','2099-01-01T00:00:00+0000');text(g,-1,'time_coverage_end','2099-01-01T00:00:12+0000')
            gt=(C.c_double*6)(-120,.01,0,38,0,-.01);call('nc_put_att_double',g,-1,b'geotransform',6,C.c_size_t(6),gt)
            height,width=(120,100) if variant=='limits' else (3,4);gh,gw=(16,16) if variant=='limits' else (2,4)
            rows=dim(g,'downtrack',height);cols=dim(g,'crosstrack',width);bands=dim(g,'bands',11 if variant=='old-mask' else 7 if role=='MASK' else 285)
            loc=group(g,'location');gy=dim(loc,'ortho_y',gh);gx=dim(loc,'ortho_x',gw)
            lat=var(loc,'lat',6,[rows,cols],'degrees north',-9999);lon=var(loc,'lon',6,[rows,cols],'degrees east',-9999)
            xx=var(loc,'glt_x',4,[gy,gx],'pixel location',0);yy=var(loc,'glt_y',4,[gy,gx],'pixel location',0)
            sensor=group(g,'sensor_band_parameters');size=11 if variant=='old-mask' else 7
            if role=='MASK':
                cube=var(g,'mask',5,[rows,cols,bands],'unitless',-9999);packed=dim(g,'packed_wavelength_bands',36);bm=var(g,'band_mask',7,[rows,cols,packed],'unitless',241);labels=var(sensor,'mask_bands',12,[bands])
            else:
                cube=var(g,'reflectance' if role=='RFL' else 'reflectance_uncertainty',5,[rows,cols,bands],None if variant=='missing-units' else 'unitless',-9999)
                call('nc_def_var_chunking',g,cube,0,(C.c_size_t*3)(1,1,285));call('nc_def_var_deflate',g,cube,1,1,4)
                wave=var(sensor,'wavelengths',5,[bands],'nm',-9999);fw=var(sensor,'fwhm',5,[bands],'nm',-9999);good=var(sensor,'good_wavelengths',7,[bands],'unitless',241)
                if variant=='scaled':number(g,cube,'scale_factor',2 if role=='RFL' else .5,6);number(g,cube,'add_offset',-.1 if role=='RFL' else 0,6)
            call('nc_enddef',g)
            lats=[38-r*.01+c*.0001 for r in range(height) for c in range(width)];lons=[-120+c*.01+r*.0002 for r in range(height) for c in range(width)]
            if variant=='shifted':lats[0]+=.001
            write(loc,lat,6,lats);write(loc,lon,6,lons);write(loc,xx,4,[2 if variant=='glt' else 1,2,0,-1,4,1,6,2]+[0]*(gh*gw-8));write(loc,yy,4,[1,1,0,2,3,3,1,2]+[0]*(gh*gw-8))
            if role=='MASK':
                q=[0.0]*(height*width*size)
                for p in range(height*width):q[p*size+4]=.2;q[p*size+6]=-1
                q[size]=1 # pixel (0,1) cloud
                q[2*size+4]=.9 # probability alone is not a binary mask
                q[3*size+2]=1 # water is optional
                q[4*size+5]=1 # actual SpecTf cloud flag
                q[5*size]=-9999 # unknown QA
                q[6*size]=.5 # invalid binary flag, never positive readiness
                bits=[0]*(height*width*36);bits[1]=128 # channel 8, big bit-order
                if variant=='missing-packed':bits[:36]=[241]*36
                names=LABELS if size==7 else [*LABELS,'legacy-a','legacy-b','legacy-c','legacy-d']
                if variant=='labels':names=[*names];names[4],names[5]=names[5],names[4]
                if variant=='oversized-string':names=[*names];names[0]='x'*5000
                write(g,cube,5,q);write(g,bm,7,bits);write(sensor,labels,12,[s.encode() for s in names])
            else:
                if variant!='limits':
                    values=[.125+p*.01+b*.001 if role=='RFL' else .005+b*.00001 for p in range(height*width) for b in range(285)]
                    values[10 if role=='RFL' else 11]=-9999
                    if variant=='extremes':values[:4]=[-.02,1.2,float('nan'),float('inf')] if role=='RFL' else [.005,-.003,-9999,.005]
                    write(g,cube,5,values)
                write(sensor,wave,5,[400+b*7.4+(.01 if variant=='spectral' and b==0 else 0) for b in range(285)]);write(sensor,fw,5,[8]*285);write(sensor,good,7,[0 if b==9 else 1 for b in range(285)])
        finally:call('nc_close',g)
        results.append({'file':path.relative_to(ROOT).as_posix(),'bytes':path.stat().st_size,'sha256':hashlib.sha256(path.read_bytes()).hexdigest()})
if (ROOT/'provenance.json').exists():results=json.loads((ROOT/'provenance.json').read_text())['files']+results
(ROOT/'provenance.json').write_text(json.dumps({'evidenceClass':'Synthetic NetCDF-C; official-writer-aligned contract tests, NOT NASA original numerical acceptance','date':'2099-01-01 intentionally synthetic','generator':'generate_fixtures.py','files':results},indent=2)+'\n')
print(len(results),'synthetic originals generated.')
