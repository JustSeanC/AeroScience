"""Hash-verify PureHDF source and apply exact, source-pinned read restrictions.

Default policy preserves historical research; --recipe selects the reviewed bounded foundation.
Neither receipt asserts NASA product/import acceptance. No network access or installer modification.
The caller supplies an exact source tree.
Never edit upstream or reuse/overwrite an existing generated source directory.
"""
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import tempfile

ROOT = Path(__file__).resolve().parents[2]
RECIPE = ROOT / 'tests/AeroScience.SafeHdfSource/upstream-source.json'


def digest(data):
    return hashlib.sha256(data).hexdigest()


def ordinary(path):
    for item in (path, *path.parents):
        if item.is_symlink() or (hasattr(item, 'is_junction') and item.is_junction()):
            raise ValueError('Linked source/output paths are not allowed.')
    return path


def replace_once(source, old, new):
    if source.count(old) != 1:
        raise ValueError('Exact reviewed upstream policy site changed; no approximate patch.')
    return source.replace(old, new)


def prepare(upstream, output, recipe_path=None):
    upstream, output = ordinary(Path(upstream).absolute()), ordinary(Path(output).absolute())
    if output.exists():
        raise ValueError('Preserve the generated policy source; choose an unused output directory.')
    recipe = json.loads((recipe_path or RECIPE).read_text(encoding='utf-8'))
    source = upstream / 'src/PureHDF'
    inventory = {p.relative_to(source).as_posix(): p for p in source.rglob('*.cs') if not {'bin', 'obj'}.intersection(p.relative_to(source).parts)}
    if set(inventory) != set(recipe['files']):
        raise ValueError('Pinned upstream C# source inventory changed.')
    values = {}
    for name, path in inventory.items():
        data = ordinary(path).read_bytes()
        if len(data) > 262144 or digest(data) != recipe['files'][name]:
            raise ValueError('Pinned upstream source hash/size changed: ' + name)
        values[name] = data
    for name, expected in recipe['notices'].items():
        if digest(ordinary(upstream / name).read_bytes()) != expected:
            raise ValueError('Pinned dependency notice changed.')
    def patch(name, old, new):
        # Upstream uses CRLF. Normalize only changed files and record both hashes.
        text = values[name].decode('utf-8').replace('\r\n', '\n')
        values[name] = replace_once(text, old, new).encode('utf-8')
    group = 'VOL/Native/API.Reading/NativeGroup.cs'
    patch(group, '''SoftLinkInfo _ => new SymbolicLink(linkMessage, this, Context.File)
                .GetTarget(linkAccess),''', '''SoftLinkInfo _ => throw new NotSupportedException("AeroScience rejects symbolic HDF links before resolution."),''')
    patch(group, '''ExternalLinkInfo _ => new SymbolicLink(linkMessage, this, Context.File)
                .GetTarget(linkAccess),''', '''ExternalLinkInfo _ => throw new NotSupportedException("AeroScience rejects external HDF links before resolution."),''')
    patch(group, '''SymbolicLinkScratchPad linkScratch => new SymbolicLink(
                name,
                heap.GetObjectName(linkScratch.LinkValueOffset),
                this,
                Context.File).GetTarget(linkAccess),''', '''SymbolicLinkScratchPad _ => throw new NotSupportedException("AeroScience rejects symbolic HDF links before resolution."),''')
    dataset = 'VOL/Native/API.Reading/NativeDataset.cs'
    patch(dataset, '''InternalExternalFileList = (ExternalFileListMessage)message.Data;''', '''throw new NotSupportedException("AeroScience rejects external HDF raw storage before resolution.");''')
    patch(dataset, '''if (InternalDataLayout is null)
            throw new Exception("The data layout message is missing.");''', '''if (InternalDataLayout is null)
            throw new Exception("The data layout message is missing.");
        if (InternalDataLayout.LayoutClass == LayoutClass.VirtualStorage)
            throw new NotSupportedException("AeroScience rejects HDF virtual storage before resolution.");''')
    driver = 'VFD/H5FileHandleDriver.Reading.cs'
    patch(driver, '''RandomAccess.Read(_handle, buffer, Position);
        _position.Value += size;''', '''// Do not decode short metadata from uninitialized stack bytes.
        ReadCore(buffer);''')
    native_file = 'VOL/Native/API.Reading/NativeFile.cs'
    patch(native_file, '''        if (!BitConverter.IsLittleEndian)
            throw new Exception("This library only works on little endian systems.");''', '''        try
        {
        if (!BitConverter.IsLittleEndian)
            throw new Exception("This library only works on little endian systems.");''')
    patch(native_file, '''        return file;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]''', '''        return file;
        }
        catch
        {
            // A failed constructor cannot leak a locked source handle on Windows.
            driver.Dispose();
            throw;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]''')
    if recipe.get('boundedDecoder'):
        patch('Filters/H5Filter.cs', '            using var sourceStream = new MemorySpanStream(info.Buffer);\n            using var decompressedStream = new MemoryStream(capacity: info.ChunkSize /* minimum size to expect */);\n            using var decompressionStream = new ZLibStream(sourceStream, CompressionMode.Decompress);\n\n            decompressionStream.CopyTo(decompressedStream);', '            if (info.ChunkSize < 1 || info.ChunkSize > 4194304)\n                throw new NotSupportedException("AeroScience rejects decompressed HDF chunks above 4 MiB.");\n            using var sourceStream = new MemorySpanStream(info.Buffer);\n            using var decompressedStream = new MemoryStream(capacity: info.ChunkSize);\n            using var decompressionStream = new ZLibStream(sourceStream, CompressionMode.Decompress);\n            var scratch = new byte[8192];\n            int read;\n            while ((read = decompressionStream.Read(scratch, 0, scratch.Length)) != 0)\n            {\n                if (decompressedStream.Length + read > info.ChunkSize + 4L)\n                    throw new NotSupportedException("AeroScience rejects decompressed HDF chunks above their declared size.");\n                decompressedStream.Write(scratch, 0, read);\n            }\n            // Fletcher32 may precede deflate in the publisher\'s filter order.\n            // Its four intermediate checksum bytes are removed by that filter;\n            // the final decoded chunk is checked exactly in H5D_Chunk.')
        patch('VOL/Native/H5D/H5D_Chunk.cs', '                        (int)ChunkByteSize,\n                        buffer);', '                        (int)ChunkByteSize,\n                        buffer);\n                    if (chunk.Length != (int)ChunkByteSize)\n                        throw new InvalidDataException("HDF decoded chunk length differs from its declared size.");')
        patch('VOL/Native/API.Reading/NativeDataset.cs', '        if (InternalDataLayout.LayoutClass == LayoutClass.VirtualStorage)\n            throw new NotSupportedException("AeroScience rejects HDF virtual storage before resolution.");', '        if (InternalDataLayout.LayoutClass == LayoutClass.VirtualStorage)\n            throw new NotSupportedException("AeroScience rejects HDF virtual storage before resolution.");\n        if (InternalFilterPipeline is { } pipeline &&\n            (pipeline.FilterDescriptions.Length > 3 ||\n             pipeline.FilterDescriptions.Any(f => f.Identifier is not (1 or 2 or 3) || f.ClientData.Length > 4) ||\n             pipeline.FilterDescriptions.Select(f => f.Identifier).Distinct().Count() != pipeline.FilterDescriptions.Length))\n            throw new NotSupportedException("AeroScience rejects unsupported HDF filter pipelines before decoding.");')
    for name, data in values.items():
        if digest(data) != recipe['patchedSha256'].get(name, recipe['files'][name]):
            raise ValueError('Prepared policy source does not match its reviewed hash.')
    receipt = {'formatVersion': 1, 'evidenceClass': 'Pinned source-level restrictions; not production importer or genuine NASA acceptance',
               'upstreamCommit': recipe['commit'], 'assembly': 'AeroScience.Hdf.SafeRead', 'policyVersion': recipe.get('policyVersion', 1),
               'files': [{'name': name, 'originalSha256': recipe['files'][name], 'preparedSha256': digest(data), 'bytes': len(data)} for name, data in sorted(values.items())],
               'productionApproved': False}
    output.parent.mkdir(parents=True, exist_ok=True)
    staging = Path(tempfile.mkdtemp(prefix='hdf-policy-', dir=output.parent))
    try:
        for name, data in values.items():
            file = staging / 'Source' / name
            file.parent.mkdir(parents=True, exist_ok=True)
            file.write_bytes(data)
        for name in recipe['notices']:
            shutil.copyfile(upstream / name, staging / name)
        (staging / 'policy-receipt.json').write_text(json.dumps(receipt, indent=2)+'\n', encoding='utf-8')
        staging.rename(output)
    finally:
        if staging.exists():
            shutil.rmtree(staging)
    return {'upstreamCommit': recipe['commit'], 'files': len(values), 'patchedFiles': [r['name'] for r in receipt['files'] if r['originalSha256'] != r['preparedSha256']], 'productionApproved': False}


def verify(output, recipe_path=None):
    output = ordinary(Path(output).absolute())
    recipe = json.loads((recipe_path or RECIPE).read_text(encoding='utf-8'))
    files = {p.relative_to(output / 'Source').as_posix(): p for p in (output / 'Source').rglob('*') if p.is_file() or p.is_symlink()}
    if set(files) != set(recipe['files']):
        raise ValueError('Generated restricted-source inventory changed.')
    for name, file in files.items():
        if digest(ordinary(file).read_bytes()) != recipe['patchedSha256'].get(name, recipe['files'][name]):
            raise ValueError('Generated restricted-source hash changed: ' + name)
    for name, expected in recipe['notices'].items():
        if digest(ordinary(output / name).read_bytes()) != expected:
            raise ValueError('Generated dependency notice changed.')
    receipt = json.loads(ordinary(output / 'policy-receipt.json').read_text(encoding='utf-8'))
    if receipt['upstreamCommit'] != recipe['commit'] or receipt['productionApproved'] is not False:
        raise ValueError('Generated source policy identity changed.')
    return {'verifiedFiles': len(files), 'productionApproved': False}


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source', type=Path)
    parser.add_argument('--verify', action='store_true')
    parser.add_argument('--recipe', type=Path, help='Explicit reviewed production policy; default preserves historical research policy.')
    parser.add_argument('--output', required=True, type=Path)
    args = parser.parse_args()
    if not args.verify and args.source is None:
        parser.error("--source is required when preparing.")
    print(json.dumps(verify(args.output,args.recipe) if args.verify else prepare(args.source, args.output,args.recipe)))
