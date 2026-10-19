"""Developer acceptance download: only the two genuine Cuprite members of NV5's public ZIP.

Uses validated HTTP ranges, streaming decompression, original CRC and pinned original-file SHA-256.
Not an AeroScience user runtime dependency or a hyperspectral global-coverage claim.
"""
import argparse
from datetime import datetime, timezone
import hashlib
import io
import json
from pathlib import Path
import re
import struct
import urllib.request
import zipfile
import zlib

URL = "https://vis-webcontent.s3.amazonaws.com/tutorials/hyperspectral.zip"
ORIGINALS = {
    "CupriteReflectance.dat": (140547456, "4cd54e7ef001bb7bcacde51712d2d1d69a338893403303f7d1d1e0da98eb1666"),
    "CupriteReflectance.hdr": (17526, "095d7cd9d3221b15eca9da0050688de3480a1321d560925c02ac58e114f87d0b"),
}


class RemoteArchive(io.RawIOBase):
    def __init__(self):
        with urllib.request.urlopen(urllib.request.Request(URL, method="HEAD"), timeout=45) as response:
            self.length = int(response.headers["Content-Length"])
        if not 1 <= self.length <= 536870912:
            raise ValueError("Unexpected original archive size")
        self.position = 0
        self.network_bytes = 0

    def seekable(self): return True
    def readable(self): return True
    def tell(self): return self.position

    def seek(self, offset, whence=io.SEEK_SET):
        self.position = offset if whence == io.SEEK_SET else self.position + offset if whence == io.SEEK_CUR else self.length + offset
        if self.position < 0:
            raise ValueError("Invalid ZIP seek")
        return self.position

    def read(self, size=-1):
        size = self.length - self.position if size < 0 else min(size, self.length - self.position)
        if size == 0: return b""
        if size > 16777216: raise ValueError("Bounded range reads are limited to 16 MiB")
        start = self.position
        request = urllib.request.Request(URL, headers={"Range": f"bytes={start}-{start + size - 1}", "Accept-Encoding": "identity"})
        with urllib.request.urlopen(request, timeout=90) as response:
            match = re.fullmatch(r"bytes (\d+)-(\d+)/(\d+)", response.headers.get("Content-Range", ""))
            if response.status != 206 or not match or tuple(map(int, match.groups())) != (start, start + size - 1, self.length):
                raise ValueError("Provider did not honor the requested bounded range")
            data = response.read(size + 1)
        if len(data) != size: raise ValueError("Truncated/oversized ZIP range")
        self.position += size
        self.network_bytes += size
        return data


def download(output):
    output = Path(output)
    if output.exists() and any(output.iterdir()): raise ValueError("Use a new empty output directory; originals were not overwritten")
    output.mkdir(parents=True, exist_ok=True)
    archive = RemoteArchive()
    with zipfile.ZipFile(archive) as directory:
        members = {Path(i.filename).name: i for i in directory.infolist() if Path(i.filename).name in ORIGINALS}
    if set(members) != set(ORIGINALS): raise ValueError("The official tutorial archive no longer has the expected originals")
    files = []
    for name, (expected_bytes, expected_hash) in ORIGINALS.items():
        member = members[name]
        if member.file_size != expected_bytes or member.compress_size > 167772160 or member.compress_type not in (zipfile.ZIP_STORED, zipfile.ZIP_DEFLATED):
            raise ValueError("Unsupported/unexpected original ZIP member")
        archive.seek(member.header_offset)
        header = archive.read(30)
        if header[:4] != b"PK\x03\x04": raise ValueError("Invalid original local ZIP header")
        names, extra = struct.unpack_from("<HH", header, 26)
        archive.seek(member.header_offset + 30 + names + extra)
        remaining = member.compress_size
        decompress = zlib.decompressobj(-15) if member.compress_type == zipfile.ZIP_DEFLATED else None
        crc = 0
        written = 0
        hash_value = hashlib.sha256()
        target = output / (name + ".partial")
        with target.open("xb") as stream:
            while remaining:
                compressed = archive.read(min(16777216, remaining))
                remaining -= len(compressed)
                data = decompress.decompress(compressed,expected_bytes-written+1) if decompress else compressed
                written += len(data)
                if written > expected_bytes: raise ValueError("Expanded original exceeds the pinned bound")
                stream.write(data)
                hash_value.update(data)
                crc = zlib.crc32(data, crc)
            if decompress:
                tail = decompress.flush()
                stream.write(tail)
                written += len(tail)
                hash_value.update(tail)
                crc = zlib.crc32(tail, crc)
        if written != expected_bytes or hash_value.hexdigest() != expected_hash or crc != member.CRC or decompress and not decompress.eof:
            raise ValueError("Original file size, SHA-256, CRC or deflate completion failed")
        target.rename(output / name)
        files.append({"relativePath": name, "bytes": written, "sha256": expected_hash})
        print("Verified actual original: " + name, flush=True)
    receipt = {"formatVersion": 1, "url": URL, "checkedUtc": datetime.now(timezone.utc).isoformat(), "archiveBytes": archive.length, "networkBytes": archive.network_bytes, "method": "validated HTTP member ranges; original ZIP CRC and pinned file SHA-256; no other tutorial scenes downloaded", "files": files}
    (output / "download-receipt.json").write_text(json.dumps(receipt, indent=2) + "\n")
    return receipt


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    result = download(args.output)
    print(f"Downloaded {result['networkBytes']} bytes of genuine member ranges from a {result['archiveBytes']}-byte official tutorial archive.")
