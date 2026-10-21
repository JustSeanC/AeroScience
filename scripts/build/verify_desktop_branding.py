#!/usr/bin/env python3
"""Verify actual compiled PE icon frames and embedded managed branding, without launching it."""
import argparse
from pathlib import Path
import struct
ROOT=Path(__file__).resolve().parents[2]
BRAND=ROOT/'src/AeroScience.Engine/wwwroot/branding'

def icon_frames(data):
    reserved,kind,count=struct.unpack_from('<HHH',data)
    if (reserved,kind)!=(0,1) or not 1<=count<=20: raise ValueError('Invalid ICO resource.')
    frames={}
    for i in range(count):
        width,height,_,_,_,depth,size,offset=struct.unpack_from('<BBBBHHII',data,6+16*i)
        if depth!=32 or width!=height or offset+size>len(data): raise ValueError('Invalid ICO frame bounds.')
        frames[width or 256]=data[offset:offset+size]
    return frames

def native_icon_frames(image):
    pe=struct.unpack_from('<I',image,60)[0]
    if image[:2]!=b'MZ' or image[pe:pe+4]!=b'PE\0\0':raise ValueError('Expected actual Windows PE.')
    count=struct.unpack_from('<H',image,pe+6)[0];optional=pe+24;opt_size=struct.unpack_from('<H',image,pe+20)[0]
    if struct.unpack_from('<H',image,optional)[0]!=0x20b:raise ValueError('Expected Windows x64 PE32+.')
    resource_rva=struct.unpack_from('<I',image,optional+112+16)[0]
    def at(rva):
        for i in range(count):
            section=optional+opt_size+40*i
            virtual_size,virtual,raw_size,raw=struct.unpack_from('<IIII',image,section+8)
            if virtual<=rva<virtual+max(virtual_size,raw_size):return raw+rva-virtual
        raise ValueError('PE resource RVA is outside sections.')
    base=at(resource_rva)
    def entries(offset):
        named,ids=struct.unpack_from('<HH',image,base+offset+12)
        return dict(struct.unpack_from('<II',image,base+offset+16+8*i) for i in range(named+ids))
    def descend(offset):
        if not offset&0x80000000:raise ValueError('Missing resource directory.')
        return entries(offset&0x7fffffff)
    root=entries(0);icons=descend(root[3]);groups=descend(root[14])
    def payload(languages):
        leaf=next(iter(languages.values()));rva,size=struct.unpack_from('<II',image,base+leaf)
        offset=at(rva);return image[offset:offset+size]
    group=payload(descend(next(iter(groups.values()))));reserved,kind,count=struct.unpack_from('<HHH',group)
    if (reserved,kind)!=(0,1):raise ValueError('Invalid native icon group.')
    result={}
    for i in range(count):
        width,height,_,_,_,depth,size,identifier=struct.unpack_from('<BBBBHHIH',group,6+14*i)
        data=payload(descend(icons[identifier]))
        if width!=height or depth!=32 or len(data)!=size:raise ValueError('Native icon frame mismatch.')
        result[width or 256]=data
    return result

def verify(folder):
    expected=icon_frames((BRAND/'aeroscience.ico').read_bytes())
    if native_icon_frames((folder/'AeroScience.exe').read_bytes())!=expected:raise ValueError('Compiled EXE icon differs from approved frames.')
    managed=(folder/'AeroScience.dll').read_bytes()
    for name in ('aeroscience.ico','primary-64.png'):
        if (BRAND/name).read_bytes() not in managed:raise ValueError('Managed window branding resource missing: '+name)
    return sorted(expected)
if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('--folder',type=Path,required=True);args=parser.parse_args()
    print('Verified compiled Windows EXE/window branding frames:',verify(args.folder),'; structural resources only, not visual Windows acceptance.')
