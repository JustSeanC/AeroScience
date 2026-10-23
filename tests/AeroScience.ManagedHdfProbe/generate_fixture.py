import ctypes as C
from pathlib import Path
n=C.CDLL('libnetcdf.so.22')
def call(name,*args):
 code=getattr(n,name)(*args)
 if code:raise ValueError(name+' failed: '+str(code))
def dimension(group,name,size):
 result=C.c_int();call('nc_def_dim',group,name.encode(),C.c_size_t(size),C.byref(result));return result.value
def variable(group,name,kind,dims):
 result=C.c_int();call('nc_def_var',group,name.encode(),kind,len(dims),(C.c_int*len(dims))(*dims),C.byref(result));return result.value
path=Path(__file__).resolve().parent / 'synthetic-netcdf4.nc';root=C.c_int();call('nc_create',str(path).encode(),0x1000,C.byref(root));root=root.value
try:
 d=dimension(root,'downtrack',2);s=dimension(root,'crosstrack',3);b=dimension(root,'bands',4)
 reflectance=variable(root,'reflectance',5,[d,s,b]);call('nc_def_var_deflate',root,reflectance,1,1,4);call('nc_def_var_fletcher32',root,reflectance,1)
 fill=C.c_float(-9999);call('nc_put_att_float',root,reflectance,b'_FillValue',5,C.c_size_t(1),C.byref(fill))
 text=b'SYNTHETIC feasibility fixture, not NASA observations';call('nc_put_att_text',root,-1,b'title',C.c_size_t(len(text)),text)
 group=C.c_int();call('nc_def_grp',root,b'sensor_band_parameters',C.byref(group));w=variable(group.value,'wavelengths',5,[b]);call('nc_put_att_text',group,w,b'units',C.c_size_t(2),b'nm')
 location=C.c_int();call('nc_def_grp',root,b'location',C.byref(location));x=variable(location,'glt_x',4,[d,s]);y=variable(location,'glt_y',4,[d,s]);call('nc_enddef',root)
 values=[i/100 for i in range(24)];values[22]=-9999;call('nc_put_var_float',root,reflectance,(C.c_float*24)(*values));call('nc_put_var_float',group,w,(C.c_float*4)(400,500,700,900));call('nc_put_var_int',location,x,(C.c_int*6)(1,2,3,1,0,3));call('nc_put_var_int',location,y,(C.c_int*6)(1,1,1,2,0,2))
finally:call('nc_close',root)
print(path,path.stat().st_size)
