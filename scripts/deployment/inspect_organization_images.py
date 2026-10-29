"""Inspect already-built local images for a private distribution review. Never builds, pulls, pushes or deploys."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess

ROLES = ('base', 'operations', 'railway', 'database')
SOURCE = 'https://github.com/JustSeanC/AeroScience'


def verify_images(images, revision):
    if not re.fullmatch('[a-f0-9]{40}', revision or '') or set(images) != set(ROLES):
        raise ValueError('Supply an exact source revision and all four image roles.')
    verified = {}
    platform = None
    for role in ROLES:
        image = images[role]
        config = image.get('Config') or {}
        labels = config.get('Labels') or {}
        identity = image.get('Id')
        current = (image.get('Os'), image.get('Architecture'))
        layers = (image.get('RootFS') or {}).get('Layers')
        if (labels.get('org.opencontainers.image.revision') != revision
                or labels.get('com.aeroscience.organization.role') != role
                or labels.get('org.opencontainers.image.source') != SOURCE
                or labels.get('org.opencontainers.image.version') != '0.7.0-development'):
            raise ValueError('Image source, version or role does not match this build.')
        if current not in [('linux', 'amd64'), ('linux', 'arm64')] or platform and current != platform:
            raise ValueError('Use one supported Linux platform per private image set.')
        platform = current
        if not isinstance(identity, str) or not re.fullmatch('sha256:[a-f0-9]{64}', identity):
            raise ValueError('Missing immutable Docker config identity.')
        if not isinstance(layers, list) or not layers or len(layers) > 128 or any(not isinstance(x, str) or not re.fullmatch('sha256:[a-f0-9]{64}', x) for x in layers):
            raise ValueError('Missing or unsupported image layer identities.')
        for value in config.get('Env') or []:
            key, _, data = value.partition('=')
            if data and re.search('PASSWORD|TOKEN|SECRET|CONNECTIONSTRING|ACCESS_KEY', key, re.IGNORECASE):
                raise ValueError('Deployment credentials must not be embedded in an image.')
        if role in ('base', 'operations') and config.get('User') != '1654:1654':
            raise ValueError('The ordinary application and operations image must be non-root.')
        if role == 'railway' and (config.get('User') != '0:0' or config.get('Entrypoint') != ['dotnet', 'AeroScience.Organization.dll', '--railway-start']):
            raise ValueError('Only the fixed privilege-dropping Railway initializer is supported.')
        verified[role] = {'dockerConfigId': identity, 'registryManifestDigest': None,
                          'layersSha256': hashlib.sha256(json.dumps(layers, separators=(',', ':')).encode()).hexdigest()}
    for parent, child in [('base', 'operations'), ('operations', 'railway')]:
        original = images[parent]['RootFS']['Layers']
        if images[child]['RootFS']['Layers'][:len(original)] != original:
            raise ValueError('Derived images must contain the exact inspected parent layers.')
    if len({row['dockerConfigId'] for row in verified.values()}) != len(ROLES):
        raise ValueError('Each image role needs its own exact config identity.')
    return {'format': 'aeroscience-private-image-review', 'version': 1, 'sourceRevision': revision,
            'platform': '/'.join(platform), 'images': verified, 'imageSet': None,
            'published': False, 'publicationAuthorized': False, 'productionReady': False,
            'limitations': ['Docker config IDs are not registry manifest digests.',
                            'Labels and layer lineage are not signed build/source attestations.',
                            'Accepted runtime tests, SBOM/license/security review and explicit publication approval remain required.',
                            'Pinned base images and dependency locks do not establish bit-for-bit OS-package reproducibility.']}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source', required=True)
    parser.add_argument('--base', default='aeroscience-organization:validation')
    parser.add_argument('--operations', default='aeroscience-organization-operations:validation')
    parser.add_argument('--railway', default='aeroscience-railway-application:validation')
    parser.add_argument('--database', default='aeroscience-railway-postgres:validation')
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    try:
        values = {}
        for role in ROLES:
            reference = getattr(args, role)
            if not re.fullmatch(r'(?:aeroscience-[a-z0-9-]+:[a-z0-9_.-]+|sha256:[a-f0-9]{64}|ghcr\.io/justseanc/aeroscience-[a-z0-9-]+@sha256:[a-f0-9]{64})', reference):
                raise ValueError('Use an explicit local validation image or official pinned identity.')
            result = subprocess.run(['docker', 'image', 'inspect', reference], capture_output=True, timeout=30, check=True)
            if len(result.stdout) > 4 * 1024 * 1024:
                raise ValueError('Image metadata exceeds the bounded review format.')
            data = json.loads(result.stdout)
            if not isinstance(data, list) or len(data) != 1:
                raise ValueError('Each role requires one image.')
            values[role] = data[0]
        report = verify_images(values, args.source)
        args.output.parent.mkdir(parents=True, exist_ok=True)
        if args.output.is_symlink():
            raise ValueError('Refuse a linked report destination.')
        descriptor = os.open(args.output, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
        with os.fdopen(descriptor, 'w') as file:
            json.dump(report, file, indent=2)
            file.write('\n')
        print('Private image identities and lineage verified. Nothing was published or deployed.')
    except (OSError, ValueError, TypeError, KeyError, subprocess.SubprocessError):
        parser.exit(2, 'Image review failed. Check exact source, roles, Linux platform, protected entrypoint and parent layers. No distribution was created.\n')


if __name__ == '__main__':
    main()
