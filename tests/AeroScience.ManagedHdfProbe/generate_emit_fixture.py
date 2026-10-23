"""Synthetic NetCDF-C storage/GLT/companion fixtures. These are never NASA originals."""
import ctypes as C
from pathlib import Path
import hashlib
import json
n=C.CDLL('libnetcdf.so.22')
root_path=Path(__file__).resolve().parent
def call(name,*args):
    result=getattr(n,name)(*args)
    if result:raise ValueError(name+': '+str(result))
def dim(group,name,length):
    result=C.c_int();call('nc_def_dim',group,name.encode(),C.c_size_t(length),C.byref(result));return result.value
def var(group,name,kind,dims):
    result=C.c_int();call('nc_def_var',group,name.encode(),kind,len(dims),(C.c_int*len(dims))(*dims),C.byref(result));return result.value
def text(group,var,name,value):
    encoded=value.encode();call('nc_put_att_text',group,var,name.encode(),C.c_size_t(len(encoded)),encoded)
def floats(group,var,values):call('nc_put_var_float',group,var,(C.c_float*len(values))(*values))
def ints(group,var,values):call('nc_put_var_int',group,var,(C.c_int*len(values))(*values))
def group(parent,name):
    result=C.c_int();call('nc_def_grp',parent,name.encode(),C.byref(result));return result.value
results=[]
for family in ['reflectance','uncertainty','mask','mismatch','oversized']:
    path=root_path/('synthetic-emit-'+family+'.nc');handle=C.c_int();call('nc_create',str(path).encode(),0x1000,C.byref(handle));r=handle.value
    try:
        text(r,-1,'title','SYNTHETIC storage/semantics fixture; NOT NASA observations')
        text(r,-1,'product_version','003' if family in ['mask','mismatch'] else '002')
        text(r,-1,'scene_id','synthetic-other-scene' if family=='mismatch' else 'synthetic-same-scene')
        text(r,-1,'time_coverage_start','2022-08-10T17:42:43Z')
        rows=dim(r,'downtrack',2);cols=dim(r,'crosstrack',300000 if family=='oversized' else 3);bands=dim(r,'bands',5)
        if family=='oversized':
            cube=var(r,'reflectance',5,[rows,cols,bands]);call('nc_def_var_chunking',r,cube,0,(C.c_size_t*3)(1,300000,5));call('nc_def_var_deflate',r,cube,1,1,4)
            # Sparse six-MiB source chunk; no huge cube is materialized.
            call('nc_enddef',r)
        elif family in ['mask','mismatch']:
            flags=dim(r,'mask_bands',2);packed=dim(r,'packed_bands',1)
            quality=var(r,'mask',5,[rows,cols,flags]);packed_var=var(r,'band_mask',7,[rows,cols,packed]);names_group=group(r,'sensor_band_parameters');names=var(names_group,'mask_bands',12,[flags])
            call('nc_enddef',r);floats(r,quality,[0,0,1,0,0,0,0,1,0,0,0,0]);call('nc_put_var_uchar',r,packed_var,(C.c_ubyte*6)(0,128,0,0,0,8));strings=(C.c_char_p*2)(b'Synthetic Cloud Flag',b'Synthetic Other Flag');call('nc_put_var_string',names_group,names,strings)
        else:
            cube=var(r,'reflectance' if family=='reflectance' else 'reflectance_uncertainty',5,[rows,cols,bands]);call('nc_def_var_chunking',r,cube,0,(C.c_size_t*3)(1,1,5));call('nc_def_var_deflate',r,cube,1,1,4);call('nc_def_var_fletcher32',r,cube,1)
            text(r,cube,'units','1');fill=C.c_float(-9999);call('nc_put_att_float',r,cube,b'_FillValue',5,C.c_size_t(1),C.byref(fill))
            sensor=group(r,'sensor_band_parameters');wv=var(sensor,'wavelengths',5,[bands]);fw=var(sensor,'fwhm',5,[bands]);good=var(sensor,'good_wavelengths',4,[bands]);text(sensor,wv,'units','nm');text(sensor,fw,'units','nm')
            if family=='reflectance':
                loc=group(r,'location');gy=dim(loc,'ortho_y',3);gx=dim(loc,'ortho_x',4);xx=var(loc,'glt_x',4,[gy,gx]);yy=var(loc,'glt_y',4,[gy,gx]);lat=var(loc,'lat',6,[rows,cols]);lon=var(loc,'lon',6,[rows,cols]);gt=(C.c_double*6)(-120,.01,0,38,0,-.01);call('nc_put_att_double',r,-1,b'geotransform',6,C.c_size_t(6),gt);text(r,-1,'spatial_ref','EPSG:4326')
            call('nc_enddef',r);values=[i/100 if family=='reflectance' else .001*(i+1) for i in range(30)];values[12]=-9999;floats(r,cube,values);floats(sensor,wv,[400,500,700,900,1100]);floats(sensor,fw,[8,8,9,10,10]);ints(sensor,good,[1,1,0,1,1])
            if family=='reflectance':
                ints(loc,xx,[1,2,3,0,1,2,3,4,0,1,2,3]);ints(loc,yy,[1,1,1,0,2,2,2,2,0,1,2,2]);call('nc_put_var_double',loc,lat,(C.c_double*6)(37.99,37.99,37.99,37.98,37.98,37.98));call('nc_put_var_double',loc,lon,(C.c_double*6)(-119.99,-119.98,-119.97,-119.99,-119.98,-119.97))
    finally:call('nc_close',r)
    results.append({'file':path.name,'bytes':path.stat().st_size,'sha256':hashlib.sha256(path.read_bytes()).hexdigest()})
(root_path/'emit-fixture-provenance.json').write_text(json.dumps({'evidenceClass':'Synthetic NetCDF-C fixtures, not NASA original/product acceptance','declaredContract':'Research-only attributes; genuine EMIT metadata resolver still needs authorized original validation','files':results},indent=2)+'\n')
print(json.dumps(results))
