"""Independently decode application PNGs and compare pinned original FAA colors, no imaging library."""
import argparse
import hashlib
import json
from pathlib import Path
import struct
import zlib


def pixels(path):
    if path.is_symlink() or not 32<=path.stat().st_size<=1048576:raise ValueError('Bound the PNG original output.')
    data=path.read_bytes()
    if data[:8]!=b'\x89PNG\r\n\x1a\n':raise ValueError('PNG signature differs.')
    offset=8;compressed=bytearray();header=False;ended=False
    while offset<len(data):
        if len(data)-offset<12:raise ValueError('Short PNG chunk.')
        size=struct.unpack_from('>I',data,offset)[0]
        if size>1048576 or size>len(data)-offset-12:raise ValueError('Unbounded PNG chunk.')
        kind=data[offset+4:offset+8];payload=data[offset+8:offset+8+size]
        if zlib.crc32(kind+payload)&0xffffffff!=struct.unpack_from('>I',data,offset+8+size)[0]:raise ValueError('PNG CRC differs.')
        if kind==b'IHDR':
            if header or payload!=struct.pack('>IIBBBBB',256,256,8,6,0,0,0):raise ValueError('PNG shape/type differs.')
            header=True
        elif kind==b'IDAT':
            if not header or ended:raise ValueError('PNG chunk order differs.')
            compressed.extend(payload)
        elif kind==b'IEND':
            if size or not header:raise ValueError('Invalid PNG end.')
            ended=True
        else:raise ValueError('Unexpected application PNG chunk.')
        offset+=size+12
        if ended and offset!=len(data):raise ValueError('Trailing PNG bytes.')
    if not ended:raise ValueError('Incomplete PNG.')
    decoder=zlib.decompressobj();raw=decoder.decompress(compressed,256*1025+1)
    if len(raw)!=256*1025 or not decoder.eof or decoder.unused_data or decoder.unconsumed_tail:raise ValueError('Unbounded/short PNG pixel stream.')
    if any(raw[y*1025]!=0 for y in range(256)):raise ValueError('Unexpected application PNG row filter.')
    return b''.join(raw[y*1025+1:(y+1)*1025] for y in range(256))


def verify(folder,reference):
    records=[]
    for source in json.loads(reference.read_text())['sources']:
        name=source['file']
        if Path(name).name!=name or '\\' in name:raise ValueError('Unsafe pinned reference name.')
        path=folder/(name+'.png');rgba=pixels(path)
        for sample in source['samples']:
            x,y=sample['x'],sample['y']
            if not 0<=x<256 or not 0<=y<256:raise ValueError('Invalid reference pixel.')
            if list(rgba[(y*256+x)*4:(y*256+x)*4+4])!=sample['rgba']:raise ValueError('Independent source-native palette/RGB comparison failed: '+name)
        records.append(dict(file=name,pngSha256=hashlib.sha256(path.read_bytes()).hexdigest(),originalSha256=source['sha256'],exactOriginalColorComparisons=len(source['samples']),pngCrcValid=True))
    return dict(evidenceClass='Independent Python PNG CRC/decompression and genuine-source-derived color comparisons. Not printed-control/neatline or owner acceptance.',results=records)


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('--folder',type=Path,required=True);parser.add_argument('--reference',type=Path,required=True);parser.add_argument('--output',type=Path,required=True);args=parser.parse_args()
    result=verify(args.folder,args.reference);args.output.write_text(json.dumps(result,indent=2)+'\n');print('PASS: independent bounded PNG CRC and original color comparisons.')
