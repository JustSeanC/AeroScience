"""Independent local EMIT comparison: NetCDF-C + NumPy, not PureHDF.

Reads bounded native and GLT windows from authorized local originals only. No
login, network, import, record promotion or NASA-authenticity assertion. Scientific
samples and this comparison stay local unless the owner approves sharing them.
"""
import argparse
import base64
import ctypes as C
import ctypes.util
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import numpy as np


class Original:
    def __init__(self, path, library=None):
        name=library or ctypes.util.find_library('netcdf')
        if not name:raise RuntimeError('Independent NetCDF-C library unavailable; use an authorized scientific validation environment, not the application decoder as its own reference.')
        self.n=C.CDLL(str(name));self.handle=C.c_int()
        signatures={'nc_open':([C.c_char_p,C.c_int,C.POINTER(C.c_int)],C.c_int),'nc_close':([C.c_int],C.c_int),'nc_inq_grp_ncid':([C.c_int,C.c_char_p,C.POINTER(C.c_int)],C.c_int),'nc_inq_varid':([C.c_int,C.c_char_p,C.POINTER(C.c_int)],C.c_int),'nc_get_vara_double':([C.c_int,C.c_int,C.POINTER(C.c_size_t),C.POINTER(C.c_size_t),C.POINTER(C.c_double)],C.c_int),'nc_get_att_double':([C.c_int,C.c_int,C.c_char_p,C.POINTER(C.c_double)],C.c_int),'nc_inq_attlen':([C.c_int,C.c_int,C.c_char_p,C.POINTER(C.c_size_t)],C.c_int),'nc_get_att_text':([C.c_int,C.c_int,C.c_char_p,C.c_void_p],C.c_int),'nc_get_vara_string':([C.c_int,C.c_int,C.POINTER(C.c_size_t),C.POINTER(C.c_size_t),C.POINTER(C.c_char_p)],C.c_int),'nc_free_string':([C.c_size_t,C.POINTER(C.c_char_p)],C.c_int)}
        for key,(args,ret) in signatures.items():getattr(self.n,key).argtypes=args;getattr(self.n,key).restype=ret
        self.check(self.n.nc_open(str(path).encode(),0,C.byref(self.handle)))
    @staticmethod
    def check(status):
        if status:raise ValueError('Independent NetCDF-C failed: '+str(status))
    def variable(self, path):
        group=self.handle.value;parts=path.split('/')
        for name in parts[:-1]:
            result=C.c_int();self.check(self.n.nc_inq_grp_ncid(group,name.encode(),C.byref(result)));group=result.value
        result=C.c_int();self.check(self.n.nc_inq_varid(group,parts[-1].encode(),C.byref(result)));return group,result.value
    def read(self, path, start, count):
        size=int(np.prod(count));
        if size>32768 or size<1:raise ValueError('Independent read exceeds bounded numeric work.')
        group,var=self.variable(path);data=(C.c_double*size)();self.check(self.n.nc_get_vara_double(group,var,(C.c_size_t*len(start))(*start),(C.c_size_t*len(count))(*count),data));return np.array(data).reshape(count)
    def scalar(self, path, name, default=None):
        g,v=self.variable(path);size=C.c_size_t();status=self.n.nc_inq_attlen(g,v,name.encode(),C.byref(size))
        if status:
            if default is not None:return default
            self.check(status)
        if size.value!=1:raise ValueError('Expected original scalar metadata.')
        value=C.c_double();self.check(self.n.nc_get_att_double(g,v,name.encode(),C.byref(value)));return value.value
    def global_numbers(self,name):
        size=C.c_size_t();self.check(self.n.nc_inq_attlen(self.handle,-1,name.encode(),C.byref(size)))
        if size.value>128:raise ValueError('Independent metadata exceeds bound.')
        values=(C.c_double*size.value)();self.check(self.n.nc_get_att_double(self.handle,-1,name.encode(),values));return np.array(values)
    def text(self,path,name):
        g,v=self.variable(path);size=C.c_size_t();self.check(self.n.nc_inq_attlen(g,v,name.encode(),C.byref(size)))
        if size.value>8192:raise ValueError('Independent text metadata exceeds bound.')
        value=C.create_string_buffer(size.value+1);self.check(self.n.nc_get_att_text(g,v,name.encode(),value));return value.value.decode('utf-8')
    def labels(self):
        g,v=self.variable('sensor_band_parameters/mask_bands');data=(C.c_char_p*7)();self.check(self.n.nc_get_vara_string(g,v,(C.c_size_t*1)(0),(C.c_size_t*1)(7),data))
        try:return [s.decode('utf-8') for s in data]
        finally:self.check(self.n.nc_free_string(7,data))
    def close(self):self.check(self.n.nc_close(self.handle))


def verify(library, report, netcdf_library=None):
    root=Path(library).resolve();report=Path(report)
    definition=json.loads((report/'emit-definition.json').read_text());native=json.loads((report/'emit-native.json').read_text());geo=json.loads((report/'emit-geographic.json').read_text())
    marker=json.loads((root/'library.json').read_text());paths={};readers={};checks=0
    for source in definition['sources']:
        relative=Path(source['relativePath'])
        if relative.is_absolute() or '..' in relative.parts or '\\' in str(relative) or ':' in str(relative):raise ValueError('Source evidence path escapes its Library.')
        p=root/relative
        if not p.resolve().is_relative_to(root) or any(part.is_symlink() for part in [p,*p.parents] if part.is_relative_to(root)):raise ValueError('Source links are refused.')
        if source['libraryId']!=marker['libraryId'] or p.stat().st_size!=source['bytes'] or digest(p)!=source['sha256']:raise ValueError('Original Library identity, size or hash changed.')
        paths[source['role']]=p;readers[source['role']]=Original(p,netcdf_library)
    r,u,m=readers['RFL'],readers['RFLUNCERT'],readers['MASK']
    def equal(a,b):
        nonlocal checks
        a=np.asarray(a,dtype=np.float64);b=np.asarray(b,dtype=np.float64);np.testing.assert_array_equal(a,b);checks+=a.size
    def decoded_bytes(text,kind):return np.frombuffer(base64.b64decode(text),dtype=kind)
    def optional(values):return np.array([np.nan if v is None else v for v in values],dtype=float)
    def physical(reader,variable,raw):
        fill=reader.scalar(variable,'_FillValue');scale=reader.scalar(variable,'scale_factor',1);offset=reader.scalar(variable,'add_offset',0)
        return np.where(np.isfinite(raw)&(raw!=fill),raw*scale+offset,np.nan)
    def compare_pixel(pixel,first,count,policy):
        row,col=pixel['row'],pixel['column'];raw=r.read('reflectance',[row,col,first],[1,1,count]).ravel();unc=u.read('reflectance_uncertainty',[row,col,first],[1,1,count]).ravel();qa=m.read('mask',[row,col,0],[1,1,7]).ravel();bits=m.read('band_mask',[row,col,0],[1,1,36]).astype('uint8').ravel()
        equal(raw,decoded_bytes(pixel['originalReflectanceBytes'],'<f4'));equal(unc,decoded_bytes(pixel['originalUncertaintyBytes'],'<f4'));equal(qa,decoded_bytes(pixel['originalQualityBytes'],'<f4'));equal(bits,decoded_bytes(pixel['originalPackedBandMask'],'u1'))
        equal(np.where(np.isfinite(qa)&(qa!=m.scalar('mask','_FillValue')),qa,np.nan),optional(pixel['qualityValues']))
        pv=physical(r,'reflectance',raw);pu=physical(u,'reflectance_uncertainty',unc);equal(pv,optional(pixel['reflectance']));equal(pu,optional(pixel['uncertainty']))
        good=r.read('sensor_band_parameters/good_wavelengths',[first],[count]).ravel();flags=[0,1,2,3,5] if policy['excludeWater'] else [0,1,3,5];known=all(v in (0,1) for v in qa[flags]);excluded=np.any(qa[flags]==1)
        interpolated=np.unpackbits(bits)[first:first+count] # NASA emit_tools.band_mask: big-bit order, truncate at 285.
        valid=np.isfinite(pv)&np.isfinite(pu)&(pu>=0)&(good==1)&known&~excluded
        if policy['excludeInterpolatedBands']:valid&=interpolated==0
        if policy['excludeInterpolatedBands'] and np.all(bits==m.scalar('band_mask','_FillValue')):valid[:]=False
        lat=r.read('location/lat',[row,col],[1,1]).item();lon=r.read('location/lon',[row,col],[1,1]).item()
        valid&=np.isfinite(lat)&np.isfinite(lon)&(-90<=lat<=90)&(-180<=lon<=180)&(lat!=r.scalar('location/lat','_FillValue'))&(lon!=r.scalar('location/lon','_FillValue'))
        located=np.isfinite(lat)&np.isfinite(lon)&(-90<=lat<=90)&(-180<=lon<=180)&(lat!=r.scalar('location/lat','_FillValue'))&(lon!=r.scalar('location/lon','_FillValue'))
        equal(optional([pixel['latitude'],pixel['longitude']]),[lat,lon] if located else [np.nan,np.nan])
        equal(np.where(valid,pv,np.nan),optional(pixel['usableReflectance']));equal(np.where(valid,pu,np.nan),optional(pixel['usableUncertainty']));equal([lat,lon],np.r_[decoded_bytes(pixel['originalLatitudeBytes'],'<f8'),decoded_bytes(pixel['originalLongitudeBytes'],'<f8')])
        for companion in (u,m):equal([lat,lon],[companion.read('location/lat',[row,col],[1,1]).item(),companion.read('location/lon',[row,col],[1,1]).item()])
    try:
        if definition['maskLabels']!=m.labels():raise ValueError('Original mask labels differ from decoder metadata.')
        for variable,original in [('RFL',r),('RFLUNCERT',u),('MASK',m)]:
            if definition['variables'][variable]['units']!=original.text(definition['variables'][variable]['path'],'units'):raise ValueError('Original scientific units differ.')
        equal(definition['wavelengthNm'],r.read('sensor_band_parameters/wavelengths',[0],[285]));equal(definition['fwhmNm'],r.read('sensor_band_parameters/fwhm',[0],[285]))
        equal(r.read('sensor_band_parameters/wavelengths',[0],[285]),u.read('sensor_band_parameters/wavelengths',[0],[285]));equal(r.read('sensor_band_parameters/fwhm',[0],[285]),u.read('sensor_band_parameters/fwhm',[0],[285]))
        nw=native['window']
        if nw['rows']*nw['columns']*nw['bandCount']>32768 or len(native['pixels'])!=nw['rows']*nw['columns']:raise ValueError('Invalid native report extent.')
        for pixel in native['pixels']:compare_pixel(pixel,nw['firstBand'],nw['bandCount'],native['qualityPolicy'])
        gw=geo['window'];start=[gw['row'],gw['column']];count=[gw['rows'],gw['columns']]
        x=r.read('location/glt_x',start,count).astype('int64').ravel();y=r.read('location/glt_y',start,count).astype('int64').ravel();gt=r.global_numbers('geotransform');equal(definition['gridTransform'],gt)
        for companion in (u,m):equal(gt,companion.global_numbers('geotransform'))
        for companion in (u,m):equal(x,companion.read('location/glt_x',start,count).ravel());equal(y,companion.read('location/glt_y',start,count).ravel())
        if len(geo['cells'])!=len(x):raise ValueError('Wrong GLT report extent.')
        for i,cell in enumerate(geo['cells']):
            row=gw['row']+i//gw['columns'];col=gw['column']+i%gw['columns'];equal([cell['gridLongitude'],cell['gridLatitude']],[gt[0]+(col+.5)*gt[1],gt[3]+(row+.5)*gt[5]])
            if 1<=x[i]<=definition['columns'] and 1<=y[i]<=definition['rows']:
                equal([cell['sourceColumn'],cell['sourceRow']],[x[i]-1,y[i]-1]);compare_pixel(cell['spectrum'],gw['firstBand'],gw['bandCount'],geo['qualityPolicy'])
            elif cell['spectrum'] is not None or cell['sourceColumn'] is not None or cell['sourceRow'] is not None:raise ValueError('Invalid/zero GLT was mapped to a native observation.')
        for source in definition['sources']:
            if digest(paths[source['role']])!=source['sha256']:raise ValueError('Original changed during independent comparison.')
    finally:
        for reader in readers.values():reader.close()
    return {'evidenceClass':'Independent local-original comparison; synthetic fixtures remain synthetic, NASA original authenticity and authorization require separate retained evidence','checkedUtc':datetime.now(timezone.utc).isoformat(),'tool':'NetCDF-C bounded raw reads + NumPy, independent of PureHDF','decoderPolicy':definition['decoderPolicy'],'numericComparisons':int(checks),'status':'passed','nasaOriginalAcceptance':'not established by this comparison alone','windowsMsfs':'not tested','sourceSha256':[s['sha256'] for s in definition['sources']]}


def digest(path):
    with Path(path).open('rb') as stream:return hashlib.file_digest(stream,'sha256').hexdigest()


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('--library',required=True,type=Path);parser.add_argument('--report',required=True,type=Path);parser.add_argument('--netcdf-library',type=Path,help='Optional trusted local NetCDF-C DLL/shared library in the independent scientific environment.')
    args=parser.parse_args();output=args.report/'independent-emit-comparison.json'
    if output.exists():raise SystemExit('Preserve prior comparison evidence; use a new report directory.')
    result=verify(args.library,args.report,args.netcdf_library);output.write_text(json.dumps(result,indent=2)+'\n');print(json.dumps(result,indent=2))
