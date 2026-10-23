"""Bounded x64 PE import audit of the owned research runtime, without executing DLLs."""
import argparse
import hashlib
import json
from pathlib import Path
import struct

SYSTEM = {'kernel32.dll','ntdll.dll','ucrtbase.dll','advapi32.dll','bcrypt.dll','crypt32.dll','ws2_32.dll',
    'wldap32.dll','normaliz.dll','secur32.dll','shell32.dll','user32.dll','ole32.dll','psapi.dll','rpcrt4.dll',
    'shlwapi.dll','gdi32.dll','comdlg32.dll','winmm.dll','version.dll','iphlpapi.dll','dnsapi.dll'}

def imports(path):
    if path.is_symlink() or not 1024 <= path.stat().st_size <= 33554432:
        raise ValueError('Unexpected native size/link.')
    data=path.read_bytes()
    def u16(offset): return struct.unpack_from('<H',data,offset)[0]
    def u32(offset): return struct.unpack_from('<I',data,offset)[0]
    assert data[:2]==b'MZ'
    pe=u32(0x3c)
    assert data[pe:pe+4]==b'PE\0\0' and u16(pe+4)==0x8664
    sections=u16(pe+6);optional=pe+24
    assert sections <= 96 and u16(optional)==0x20b and u16(pe+20)>=128
    start=optional+u16(pe+20)
    def offset(rva):
        for index in range(sections):
            section=start+index*40
            virtual=u32(section+12);size=u32(section+16);raw=u32(section+20)
            if virtual <= rva < virtual+size:
                result=raw+rva-virtual
                if result >= len(data): raise ValueError('PE RVA outside file.')
                return result
        raise ValueError('Unmapped PE RVA.')
    def name(rva):
        at=offset(rva);end=data.index(0,at,min(at+256,len(data)))
        return data[at:end].decode('ascii').lower()
    result=[]
    rva=u32(optional+120)
    if rva:
        at=offset(rva)
        for index in range(256):
            descriptor=at+index*20
            if data[descriptor:descriptor+20]==b'\0'*20: break
            result.append(name(u32(descriptor+12)))
        else: raise ValueError('Unbounded PE import inventory.')
    # Delay imports must be accounted for too, rather than hidden by a successful fixture load.
    delay=u32(optional+112+13*8)
    if delay:
        at=offset(delay)
        for index in range(256):
            descriptor=at+index*32
            if data[descriptor:descriptor+32]==b'\0'*32: break
            if u32(descriptor)!=1: raise ValueError('Unsupported delay-import addressing.')
            result.append(name(u32(descriptor+4)))
        else: raise ValueError('Unbounded PE delay-import inventory.')
    return sorted(set(result))

def verify(folder):
    files=sorted(folder.glob('*.dll'))
    if not 1 <= len(files) <= 32: raise ValueError('Bound runtime inventory.')
    names={p.name.lower() for p in files};result=[]
    for p in files:
        required=imports(p)
        for dependency in required:
            if dependency.startswith(('vcruntime','msvcp','msvcr')): raise ValueError('Unshipped VC runtime import: '+dependency)
            if dependency not in names|SYSTEM and not dependency.startswith(('api-ms-win-','ext-ms-win-')):
                raise ValueError('Unreviewed/unbundled import: '+dependency)
        result.append({'file':p.name,'bytes':p.stat().st_size,'sha256':hashlib.sha256(p.read_bytes()).hexdigest(),'imports':required})
    return {'architecture':'Windows x64','importsAccountedFor':True,'staticCrtRequired':True,'files':result,
        'limitations':['Direct/delay PE imports only; dynamic driver loading still requires actual isolated execution/security review.','This is research evidence, not SDK/MSFS or public distribution acceptance.']}

if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('--folder',type=Path,required=True);parser.add_argument('--output',type=Path,required=True);args=parser.parse_args()
    args.output.write_text(json.dumps(verify(args.folder),indent=2)+'\n')
    print('PASS: bounded x64 native imports, owned dependency inventory and no unshipped VC runtime imports.')
