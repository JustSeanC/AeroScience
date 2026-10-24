"""Bounded synthetic HDF5-C adversarial originals. No NASA acceptance claim."""
import ctypes as C
import ctypes.util
import hashlib
import json
from pathlib import Path
import zlib

root=Path(__file__).resolve().parent
h=C.CDLL(ctypes.util.find_library('hdf5_serial'));i=C.c_longlong;u=C.c_ulonglong
for name,args,result in [
 ('H5Fcreate',[C.c_char_p,C.c_uint,i,i],i),('H5Fclose',[i],C.c_int),
 ('H5Screate_simple',[C.c_int,C.POINTER(u),C.POINTER(u)],i),('H5Sclose',[i],C.c_int),
 ('H5Pcreate',[i],i),('H5Pclose',[i],C.c_int),('H5Pset_chunk',[i,C.c_int,C.POINTER(u)],C.c_int),
 ('H5Pset_deflate',[i,C.c_uint],C.c_int),('H5Dcreate2',[i,C.c_char_p,i,i,i,i,i],i),
 ('H5Dwrite_chunk',[i,i,C.c_uint,C.POINTER(u),C.c_size_t,C.c_void_p],C.c_int),('H5Dclose',[i],C.c_int)]:
 getattr(h,name).argtypes=args;getattr(h,name).restype=result
assert h.H5open()>=0
kind=i.in_dll(h,'H5T_NATIVE_UCHAR_g').value;prop=i.in_dll(h,'H5P_CLS_DATASET_CREATE_ID_g').value
path=root/'deflate-expansion.nc'
if path.exists():raise ValueError('Preserve existing generated fixtures.')
def call(name,*args):
 value=getattr(h,name)(*args)
 if value<0:raise ValueError(name)
 return value
file=call('H5Fcreate',str(path).encode(),2,0,0);space=call('H5Screate_simple',2,(u*2)(1024,1024),None);properties=call('H5Pcreate',prop)
try:
 call('H5Pset_chunk',properties,2,(u*2)(1024,1024));call('H5Pset_deflate',properties,6)
 dataset=call('H5Dcreate2',file,b'unsafe',kind,space,0,properties,0)
 try:
  compressor=zlib.compressobj(6);block=bytes(1048576);pieces=[]
  for _ in range(256):pieces.append(compressor.compress(block))
  pieces.append(compressor.flush());encoded=b''.join(pieces)
  call('H5Dwrite_chunk',dataset,0,0,(u*2)(0,0),len(encoded),C.c_char_p(encoded))
 finally:call('H5Dclose',dataset)
finally:call('H5Pclose',properties);call('H5Sclose',space);call('H5Fclose',file)
(root/'provenance.json').write_text(json.dumps({'evidenceClass':'Synthetic HDF5-C adversarial input, not NASA','file':path.name,'bytes':path.stat().st_size,'sha256':hashlib.sha256(path.read_bytes()).hexdigest(),'declaredUncompressedChunkBytes':1048576,'actualDeflateExpansionBytes':268435456,'generatorMemory':'1 MiB scratch plus bounded compressed output; no expanded 256 MiB allocation'},indent=2)+'\n')
