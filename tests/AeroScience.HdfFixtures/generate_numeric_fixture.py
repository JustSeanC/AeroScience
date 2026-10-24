import ctypes as C,ctypes.util,pathlib,json,hashlib,math
root=pathlib.Path(__file__).resolve().parent;path=root/'numeric-types.nc'
if path.exists():raise ValueError('Preserve previous fixture.')
h=C.CDLL(ctypes.util.find_library('hdf5_serial'));i=C.c_longlong;u=C.c_ulonglong
for name,args,ret in [('H5Fcreate',[C.c_char_p,C.c_uint,i,i],i),('H5Fclose',[i],C.c_int),('H5Screate_simple',[C.c_int,C.POINTER(u),C.POINTER(u)],i),('H5Sclose',[i],C.c_int),('H5Dcreate2',[i,C.c_char_p,i,i,i,i,i],i),('H5Dwrite',[i,i,i,i,i,C.c_void_p],C.c_int),('H5Dclose',[i],C.c_int)]:getattr(h,name).argtypes=args;getattr(h,name).restype=ret
assert h.H5open()>=0
file=h.H5Fcreate(str(path).encode(),2,0,0);space=h.H5Screate_simple(1,(u*1)(4),None)
try:
 for name,file_type,memory_type,ctype,values in [('double_be','H5T_IEEE_F64BE_g','H5T_NATIVE_DOUBLE_g',C.c_double,[1.25,-9999,float('nan'),2.5]),('signed','H5T_STD_I32LE_g','H5T_NATIVE_INT_g',C.c_int,[-1,0,2147483647,-2147483648]),('flags','H5T_STD_U8LE_g','H5T_NATIVE_UCHAR_g',C.c_ubyte,[0,128,255,8])]:
  dataset=h.H5Dcreate2(file,name.encode(),i.in_dll(h,file_type).value,space,0,0,0);assert dataset>=0
  try:assert h.H5Dwrite(dataset,i.in_dll(h,memory_type).value,0,0,0,(ctype*4)(*values))>=0
  finally:assert h.H5Dclose(dataset)>=0
finally:h.H5Sclose(space);h.H5Fclose(file)
(root/'numeric-provenance.json').write_text(json.dumps({'evidenceClass':'Synthetic HDF5-C typed original, not NASA','file':path.name,'bytes':path.stat().st_size,'sha256':hashlib.sha256(path.read_bytes()).hexdigest(),'expected':{'double_be':['1.25','-9999','NaN','2.5'],'signed':[-1,0,2147483647,-2147483648],'flags':[0,128,255,8]}},indent=2)+'\n')
