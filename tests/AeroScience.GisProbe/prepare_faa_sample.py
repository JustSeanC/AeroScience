"""Opt-in research fixture: one exact genuine FAA archive, never an application downloader."""
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import urllib.request
import zipfile

URL = 'https://aeronav.faa.gov/visual/09-03-2026/sectional-files/Hawaiian_Islands.zip'
EXPECTED = '351107a94c53634014410bacec43253ed4117a77f34a952207c7a7bd9112680e'
MAXIMUM_BYTES = 27291624


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *args, **kwargs):
        raise ValueError('Research fixture refuses redirects; inspect a changed official endpoint.')


def prepare(output):
    output.mkdir(parents=True, exist_ok=False)
    archive = output / 'original.zip'
    total = 0
    digest = hashlib.sha256()
    request = urllib.request.Request(URL, headers={'User-Agent': 'AeroScience/0.6.0-native-research'})
    with urllib.request.build_opener(NoRedirect).open(request, timeout=60) as response, archive.open('xb') as target:
        if response.status != 200:
            raise ValueError('Expected complete original HTTP 200, not a partial/other response.')
        while chunk := response.read(65536):
            total += len(chunk)
            if total > MAXIMUM_BYTES:
                raise ValueError('FAA research original exceeds its pinned byte bound.')
            digest.update(chunk)
            target.write(chunk)
    if total != MAXIMUM_BYTES or digest.hexdigest() != EXPECTED:
        raise ValueError('FAA original identity changed. Preserve it and investigate; do not restamp the fixture.')
    reference = json.loads((Path(__file__).parent / 'faa-window-reference.json').read_text())
    # Select only a pinned top-level member after verifying the complete original archive.
    # This is not a general-purpose ZIP extractor and never follows member paths.
    with zipfile.ZipFile(archive) as source:
        matches = [i for i in source.infolist() if i.filename == reference['file']]
        if len(matches) != 1 or matches[0].file_size > 32 * 1024 * 1024:
            raise ValueError('Unexpected FAA research member identity or size.')
        target = output / reference['file']
        with source.open(matches[0]) as input_file, target.open('xb') as output_file:
            shutil.copyfileobj(input_file, output_file, 65536)
        if hashlib.sha256(target.read_bytes()).hexdigest() != reference['sha256']:
            raise ValueError('FAA TIFF differs from the independent reference.')
    (output / 'acquisition.json').write_text(json.dumps({'url': URL, 'bytes': total,
        'sha256': digest.hexdigest(), 'evidence': 'Genuine original FAA ZIP. Not neatline or chart placement acceptance.'}, indent=2) + '\n')
    print(target)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', required=True, type=Path)
    prepare(parser.parse_args().output)
