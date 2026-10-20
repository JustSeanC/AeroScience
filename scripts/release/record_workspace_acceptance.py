"""Record actual genuine-data workspace runs and independently checked raster outputs."""
import argparse
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import zipfile
import verify_scientific_acceptance as verification


def digest(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def record(providers, envi, output):
    rasters = []
    packages = []
    raw = []
    regional_count = 0
    for folder in (providers, envi):
        workspace_path = folder / 'workspace-acceptance.json'
        comparison_path = folder / 'independent-workspace-comparison.json'
        workspace = json.loads(workspace_path.read_text(encoding='utf-8'))
        comparison = json.loads(comparison_path.read_text(encoding='utf-8'))
        if workspace['evidenceType'] != 'genuine-external-data-and-synthetic-flight-input' or workspace['windowsMsfs'] != 'not tested' or comparison['evidenceType'] != 'independent-genuine-raster-comparison':
            raise ValueError('Actual genuine-source runs and independent numeric comparisons are required')
        checked = {r['artifactId']: r for r in comparison['results']}
        catalog = {s['id']: s for s in workspace['retainedSources']}
        for item in workspace['results']:
            for artifact in item['artifacts']:
                proof = checked[artifact['id']]
                if digest(folder / 'ScienceLibrary' / artifact['relativePath']) != proof['sha256']:
                    raise ValueError('Derived raster changed after independent validation')
                metadata = json.loads(artifact['metadataJson'])
                regional_count = max(regional_count, len(metadata['sourceIds']))
                for sid in metadata['sourceIds']:
                    source = catalog[sid]
                    for f in source['files']:
                        if digest(folder / 'ScienceLibrary' / source['relativeDirectory'] / f['relativePath']) != f['sha256']:
                            raise ValueError('Retained original changed')
            package = folder / item['package']
            with zipfile.ZipFile(package) as archive:
                manifest = json.loads(archive.read('manifest.json'))
                if manifest['missionId'] != item['product']['missionId']:
                    raise ValueError('Research ZIP belongs to a different mission')
                for f in manifest['files']:
                    with archive.open(f['path']) as stream:
                        h = hashlib.file_digest(stream, 'sha256').hexdigest()
                    if h != f['sha256'] or archive.getinfo(f['path']).file_size != f['bytes']:
                        raise ValueError('Research ZIP content/hash manifest mismatch')
            packages.append(dict(missionId=item['product']['missionId'], package=package.name, sha256=digest(package)))
        rasters.extend(comparison['results'])
        raw.append(dict(workspaceSha256=digest(workspace_path), independentSha256=digest(comparison_path), tool=comparison['tool']))
    value = dict(formatVersion=1, version=verification.VERSION, evidenceType='genuine-scientific-workspace',
                 checkedUtc=datetime.now(timezone.utc).isoformat(), windowsMsfs='not tested',
                 flightInput='synthetic test positions; genuine external originals',
                 originalIntegrityVerified=True, regionalSourcesCombined=regional_count,
                 researchPackagesVerified=len(packages), packages=packages,
                 independentRasterResults=rasters, runReceipts=raw,
                 canonicalLfSourceSha256={n: verification.source_digest(verification.ROOT/n) for n in verification.SOURCES})
    output.write_text(json.dumps(value, indent=2)+'\n', encoding='utf-8', newline='\n')
    verification.verify_workspace(output)
    return value


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--providers', type=Path, required=True)
    parser.add_argument('--envi', type=Path, required=True)
    parser.add_argument('--output', type=Path, default=verification.ROOT/f'docs/research/v{verification.VERSION}-workspace-acceptance.json')
    args = parser.parse_args()
    result = record(args.providers, args.envi, args.output)
    print(f"Recorded {len(result['independentRasterResults'])} genuine raster comparisons and {result['researchPackagesVerified']} verified research packages. No Windows/MSFS claim.")
