"""Apply an exact, reviewable reduced-driver edit to the pinned vcpkg port, not product code."""
import argparse
import hashlib
import json
from pathlib import Path

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--vcpkg', required=True, type=Path)
parser.add_argument('--receipt', required=True, type=Path)
args = parser.parse_args()
port = args.vcpkg/'ports/gdal/portfile.cmake'
jpeg_port = args.vcpkg/'ports/libjpeg-turbo/portfile.cmake'
jpeg_original = jpeg_port.read_bytes().replace(b'\r\n', b'\n')
jpeg_expected = '7a3d4443ecb74757d6798e1280763da918b4ecee06a4e1c247fb2c3ec71994ff'
# Check both original recipes before mutating either. An unsupported registry must
# leave the original source tree intact, not partially prepare a different build.
if hashlib.sha256(jpeg_original).hexdigest() != jpeg_expected:
    raise SystemExit('Pinned JPEG port differs; do not patch an unreviewed recipe.')
jpeg_text = jpeg_original.decode()
for lib in ('lib', 'debug/lib'):
    command = f'        file(RENAME "${{CURRENT_PACKAGES_DIR}}/{lib}/turbojpeg-static.lib" "${{CURRENT_PACKAGES_DIR}}/{lib}/turbojpeg.lib")'
    if jpeg_text.count(command) != 1: raise SystemExit('Ambiguous optional TurboJPEG static rename.')
    jpeg_text = jpeg_text.replace(command, f'        if(EXISTS "${{CURRENT_PACKAGES_DIR}}/{lib}/turbojpeg-static.lib")\n    {command}\n        endif()')
original = port.read_bytes()
expected = '181be76ccc9326d9657020783dc573d3638e3385197964cca8f96efe8f05d841'
canonical = original.replace(b'\r\n', b'\n')
if hashlib.sha256(canonical).hexdigest() != expected:
    raise SystemExit('Pinned GDAL port differs; do not patch an unreviewed recipe.')
text = canonical.decode()
for old, new in [('GDAL_BUILD_OPTIONAL_DRIVERS=ON', 'GDAL_BUILD_OPTIONAL_DRIVERS=OFF'),
                 ('OGR_BUILD_OPTIONAL_DRIVERS=ON', 'OGR_BUILD_OPTIONAL_DRIVERS=OFF')]:
    if text.count(old) != 1:
        raise SystemExit('Ambiguous reduced-driver edit.')
    text = text.replace(old, new)
text = text.replace('-DGDAL_BUILD_OPTIONAL_DRIVERS=OFF',
                    '-DGDAL_BUILD_OPTIONAL_DRIVERS=OFF\n        -DGDAL_ENABLE_DRIVER_GTIFF=ON\n        -DGDAL_ENABLE_DRIVER_VRT=ON')
port.write_text(text, encoding='utf-8', newline='\n')
jpeg_port.write_text(jpeg_text, encoding='utf-8', newline='\n')
args.receipt.parent.mkdir(parents=True, exist_ok=True)
args.receipt.write_text(json.dumps({'vcpkgCommit': '1391150599c0b9dc6f2a3c79c50eff6c94b077d1',
    'gdalVersion': '3.12.4', 'originalPortSha256': expected,
    'reducedPortSha256': hashlib.sha256(text.encode()).hexdigest(),
    'defaultFeatures': False, 'optionalDrivers': False, 'requestedRasterDrivers': ['GTiff', 'VRT'],
    'jpegOriginalPortSha256': jpeg_expected, 'jpegReducedPortSha256': hashlib.sha256(jpeg_text.encode()).hexdigest(),
    'jpegLibraryLinkage': 'static', 'turbojpegEnabled': False, 'productionDistributionApproved': False}, indent=2)+'\n')
