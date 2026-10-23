"""Bounded x64 PE import audit of the owned research runtime, without executing DLLs."""
import argparse
import hashlib
import json
from pathlib import Path
import struct

SYSTEM = {'kernel32.dll','ntdll.dll','ucrtbase.dll','advapi32.dll','bcrypt.dll','crypt32.dll','ws2_32.dll',
    'wldap32.dll','normaliz.dll','secur32.dll','shell32.dll','user32.dll','ole32.dll','psapi.dll','rpcrt4.dll',
    'shlwapi.dll','gdi32.dll','comdlg32.dll','winmm.dll','version.dll','iphlpapi.dll','dnsapi.dll'}

def import_groups(path):
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
    direct=[]; delayed=[]
    rva=u32(optional+120)
    if rva:
        at=offset(rva)
        for index in range(256):
            descriptor=at+index*20
            if data[descriptor:descriptor+20]==b'\0'*20: break
            direct.append(name(u32(descriptor+12)))
        else: raise ValueError('Unbounded PE import inventory.')
    # Delay imports must be accounted for too, rather than hidden by a successful fixture load.
    delay=u32(optional+112+13*8)
    if delay:
        at=offset(delay)
        for index in range(256):
            descriptor=at+index*32
            if data[descriptor:descriptor+32]==b'\0'*32: break
            if u32(descriptor)!=1: raise ValueError('Unsupported delay-import addressing.')
            delayed.append(name(u32(descriptor+4)))
        else: raise ValueError('Unbounded PE delay-import inventory.')
    return {'direct':sorted(set(direct)),'delay':sorted(set(delayed))}

def imports(path):
    groups=import_groups(path)
    return sorted(set(groups['direct']+groups['delay']))


def exports(path):
    """Bounded PE named-export inventory; import_groups validates x64 headers first."""
    import_groups(path)
    data=path.read_bytes()
    def u16(at):return struct.unpack_from('<H',data,at)[0]
    def u32(at):return struct.unpack_from('<I',data,at)[0]
    pe=u32(0x3c);optional=pe+24;start=optional+u16(pe+20);sections=u16(pe+6)
    def address(rva):
        for i in range(sections):
            section=start+i*40;virtual=u32(section+12);size=u32(section+16);raw=u32(section+20)
            if virtual<=rva<virtual+size:
                at=raw+rva-virtual
                if at>=len(data):raise ValueError('Export RVA outside file.')
                return at
        raise ValueError('Unmapped export RVA.')
    rva=u32(optional+112)
    if not rva:return []
    directory=address(rva);count=u32(directory+24)
    if count>65536:raise ValueError('Unbounded PE export inventory.')
    names=address(u32(directory+32)) if count else 0
    result=[]
    for i in range(count):
        at=address(u32(names+i*4));end=data.index(0,at,min(at+4096,len(data)))
        result.append(data[at:end].decode('ascii'))
    return sorted(set(result))


def inspect(folder):
    files=sorted(folder.glob('*.dll'))
    if not 1 <= len(files) <= 32: raise ValueError('Bound runtime inventory.')
    names={p.name.lower() for p in files};result=[];errors=[]
    for p in files:
        try:
            groups=import_groups(p);required=sorted(set(groups['direct']+groups['delay']))
            symbols=exports(p);turbo=[s for s in symbols if s.startswith('tj')]
            if turbo:errors.append(f'{p.name}: Excluded TurboJPEG API unexpectedly exported: '+', '.join(turbo))
            for dependency in required:
                if dependency.startswith(('vcruntime','msvcp','msvcr')):
                    errors.append(f'{p.name}: Unshipped VC runtime import: {dependency}')
                elif dependency not in names|SYSTEM and not dependency.startswith(('api-ms-win-','ext-ms-win-')):
                    errors.append(f'{p.name}: Unreviewed/unbundled import: {dependency}')
            result.append({'file':p.name,'bytes':p.stat().st_size,'sha256':hashlib.sha256(p.read_bytes()).hexdigest(),
                           'imports':required,'directImports':groups['direct'],'delayImports':groups['delay'],
                           'exportCount':len(symbols),'exportsSha256':hashlib.sha256(('\n'.join(symbols)+'\n').encode()).hexdigest(),'turbojpegExports':turbo})
        except (ValueError,AssertionError,IndexError,struct.error) as exc:
            errors.append(f'{p.name}: Invalid native import inventory: {exc}')
    return {'architecture':'Windows x64','importsAccountedFor':not errors,'staticCrtRequired':True,
        'files':result,'errors':errors,
        'limitations':['Direct/delay PE imports only; dynamic driver loading still requires actual isolated execution/security review.',
                       'This is research evidence, not SDK/MSFS or public distribution acceptance.']}


def verify(folder):
    result=inspect(folder)
    if result['errors']: raise ValueError('; '.join(result['errors']))
    return result


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('--folder',type=Path,required=True);parser.add_argument('--output',type=Path,required=True);args=parser.parse_args()
    result=inspect(args.folder)
    args.output.write_text(json.dumps(result,indent=2)+'\n')
    if result['errors']: raise SystemExit('Native import audit failed: '+ '; '.join(result['errors']))
    print('PASS: bounded x64 native imports, owned dependency inventory and no unshipped VC runtime imports.')
