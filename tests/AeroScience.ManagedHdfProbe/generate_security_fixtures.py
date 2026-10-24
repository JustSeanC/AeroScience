"""Synthetic HDF5-C link/raw/virtual fixtures, never NASA data."""
import ctypes as C, ctypes.util, pathlib, json, hashlib
import argparse
parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('--output',required=True,type=pathlib.Path);args=parser.parse_args()
root=args.output
if root.exists():raise ValueError('Preserve existing fixtures; choose an unused output directory.')
root.mkdir(parents=True)
h=C.CDLL(ctypes.util.find_library('hdf5_serial'));id_t=C.c_longlong;size=C.c_ulonglong
for name,argtypes,ret in [
 ('H5Fcreate',[C.c_char_p,C.c_uint,id_t,id_t],id_t),('H5Fclose',[id_t],C.c_int),
 ('H5Lcreate_external',[C.c_char_p,C.c_char_p,id_t,C.c_char_p,id_t,id_t],C.c_int),
 ('H5Lcreate_soft',[C.c_char_p,id_t,C.c_char_p,id_t,id_t],C.c_int),
 ('H5Screate_simple',[C.c_int,C.POINTER(size),C.POINTER(size)],id_t),('H5Sclose',[id_t],C.c_int),
 ('H5Pcreate',[id_t],id_t),('H5Pclose',[id_t],C.c_int),
 ('H5Pset_external',[id_t,C.c_char_p,C.c_longlong,size],C.c_int),
 ('H5Pset_virtual',[id_t,id_t,C.c_char_p,C.c_char_p,id_t],C.c_int),
 ('H5Dcreate2',[id_t,C.c_char_p,id_t,id_t,id_t,id_t,id_t],id_t),('H5Dclose',[id_t],C.c_int),
]:getattr(h,name).argtypes=argtypes;getattr(h,name).restype=ret
assert h.H5open()>=0
float_type=id_t.in_dll(h,'H5T_NATIVE_FLOAT_g').value;dcpl_type=id_t.in_dll(h,'H5P_CLS_DATASET_CREATE_ID_g').value

def call(name,*args):
 result=getattr(h,name)(*args)
 if result<0:raise RuntimeError(name)
 return result
entries=[]
for name,target in [('external-traversal.nc',b'../../outside/canary.nc'),('external-absolute.nc',b'/must-not-be-opened/canary.nc'),('external-windows.nc',b'C:\\must-not-be-opened\\canary.nc'),('external-unc.nc',b'\\\\remote.invalid\\share\\canary.nc'),('external-url.nc',b'https://must-not-be-opened.invalid/canary.nc'),('soft-cycle.nc',None),('external-raw.nc',None),('virtual.nc',None)]:
 path=root/name;f=call('H5Fcreate',str(path).encode(),2,0,0)
 try:
  if target:call('H5Lcreate_external',target,b'/reflectance',f,b'unsafe',0,0)
  elif name=='soft-cycle.nc':call('H5Lcreate_soft',b'/unsafe',f,b'unsafe',0,0)
  else:
   space=call('H5Screate_simple',1,(size*1)(5),None);plist=call('H5Pcreate',dcpl_type)
   try:
    if name=='external-raw.nc':call('H5Pset_external',plist,b'../../outside/raw.bin',0,20)
    else:call('H5Pset_virtual',plist,space,b'../../outside/canary.nc',b'/reflectance',space)
    ds=call('H5Dcreate2',f,b'unsafe',float_type,space,0,plist,0);call('H5Dclose',ds)
   finally:call('H5Pclose',plist);call('H5Sclose',space)
 finally:call('H5Fclose',f)
 entries.append({'file':name,'bytes':path.stat().st_size,'sha256':hashlib.sha256(path.read_bytes()).hexdigest(),'expected': 'external link' if target else 'symbolic link' if name=='soft-cycle.nc' else 'external raw storage' if name=='external-raw.nc' else 'virtual storage'})
# The exact original failed external-link fixture is copied separately, not silently regenerated.
for name in ['reflectance','uncertainty','mask']:
 source=pathlib.Path(__file__).resolve().parent/'security-fixtures'/('previous-canary-'+name+'.nc');path=root/source.name;path.write_bytes(source.read_bytes());entries.append({'file':path.name,'bytes':path.stat().st_size,'sha256':hashlib.sha256(path.read_bytes()).hexdigest(),'expected':'previous exact failed canary'})
(root/'provenance.json').write_text(json.dumps({'evidenceClass':'Synthetic HDF5-C adversarial fixtures, no NASA originals','generatorLibrary':ctypes.util.find_library('hdf5_serial'),'files':entries},indent=2)+'\n')
print(len(entries),'fixtures')
