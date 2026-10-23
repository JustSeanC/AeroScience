"""Record actual genuine-data workspace runs and independently checked raster outputs."""
import argparse
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import zipfile
import verify_scientific_acceptance as verification
from scientific_evidence import load_run,require_artifact,refuse_secrets,safe_file


def digest(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def record(providers, envi, output):
    rasters = []
    packages = []
    raw = []
    regional_count = 0
    runs=[];hashes=None;seen_artifacts=set();seen_missions=set()
    for folder in (providers, envi):
        run,provenance=load_run(folder,'providers' if folder==providers else 'envi');runs.append(provenance)
        hashes=run['canonicalLfSourceSha256']
        workspace_path = require_artifact(folder,run,'workspace-acceptance.json')
        comparison_path = require_artifact(folder,run,'independent-workspace-comparison.json')
        workspace = json.loads(workspace_path.read_text(encoding='utf-8'))
        comparison = json.loads(comparison_path.read_text(encoding='utf-8'))
        if workspace['evidenceType'] != 'genuine-external-data-and-synthetic-flight-input' or workspace['windowsMsfs'] != 'not tested' or comparison['evidenceType'] != 'independent-genuine-raster-comparison':
            raise ValueError('Actual genuine-source runs and independent numeric comparisons are required')
        checked = {r['artifactId']: r for r in comparison['results']}
        if len(checked)!=len(comparison['results']) or set(checked)!={a['id'] for item in workspace['results'] for a in item['artifacts']}:raise ValueError('Independent rasters must match the actual distinct workspace artifacts.')
        catalog = {s['id']: s for s in workspace['retainedSources']}
        for item in workspace['results']:
            for artifact in item['artifacts']:
                if artifact['id'] in seen_artifacts:raise ValueError('Duplicate raster cannot inflate independent evidence.')
                seen_artifacts.add(artifact['id'])
                proof = checked[artifact['id']]
                if digest(safe_file(folder/'ScienceLibrary',artifact['relativePath'])) != proof['sha256']:
                    raise ValueError('Derived raster changed after independent validation')
                metadata = json.loads(artifact['metadataJson'])
                regional_count = max(regional_count, len(set(metadata['sourceIds'])))
                for sid in metadata['sourceIds']:
                    source = catalog[sid]
                    for f in source['files']:
                        if digest(safe_file(folder/'ScienceLibrary',str(Path(source['relativeDirectory'])/f['relativePath']))) != f['sha256']:
                            raise ValueError('Retained original changed')
            package = safe_file(folder,item['package'])
            if item['product']['missionId'] in seen_missions:raise ValueError('Duplicate mission cannot inflate verified research packages.')
            seen_missions.add(item['product']['missionId'])
            with zipfile.ZipFile(package) as archive:
                manifest = json.loads(archive.read('manifest.json'))
                if manifest['missionId'] != item['product']['missionId']:
                    raise ValueError('Research ZIP belongs to a different mission')
                for f in manifest['files']:
                    with archive.open(f['path']) as stream:
                        h = hashlib.file_digest(stream, 'sha256').hexdigest()
                    if h != f['sha256'] or archive.getinfo(f['path']).file_size != f['bytes']:
                        raise ValueError('Research ZIP content/hash manifest mismatch')
                if set(archive.namelist())!={f['path'] for f in manifest['files']}|{'manifest.json'}:raise ValueError('Unlisted research ZIP content.')
            packages.append(dict(missionId=item['product']['missionId'], package=package.name, sha256=digest(package)))
        rasters.extend(comparison['results'])
        raw.append(dict(workspaceSha256=digest(workspace_path), independentSha256=digest(comparison_path), tool=comparison['tool']))
    value = dict(formatVersion=1, version=verification.VERSION, evidenceType='genuine-scientific-workspace',
                 checkedUtc=datetime.now(timezone.utc).isoformat(), windowsMsfs='not tested',
                 flightInput='synthetic test positions; genuine external originals',
                 originalIntegrityVerified=True, regionalSourcesCombined=regional_count,
                 researchPackagesVerified=len(packages), packages=packages,
                 independentRasterResults=rasters, runReceipts=raw,
                 canonicalLfSourceSha256=hashes,runProvenance=runs)
    refuse_secrets(value)
    verification.write_verified(output,value,verification.verify_workspace)
    return value


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--providers', type=Path, required=True)
    parser.add_argument('--envi', type=Path, required=True)
    parser.add_argument('--output', type=Path, default=verification.ROOT/f'docs/research/v{verification.VERSION}-workspace-acceptance.json')
    args = parser.parse_args()
    result = record(args.providers, args.envi, args.output)
    print(f"Recorded {len(result['independentRasterResults'])} genuine raster comparisons and {result['researchPackagesVerified']} verified research packages. No Windows/MSFS claim.")
