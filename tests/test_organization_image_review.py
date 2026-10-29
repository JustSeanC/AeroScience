"""Negative distribution checks; actual built images are additionally checked by the Full container job."""
import copy
import importlib.util
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location('image_review', ROOT / 'scripts/deployment/inspect_organization_images.py')
REVIEW = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(REVIEW)


def fixture():
    result = {}
    for index, role in enumerate(REVIEW.ROLES):
        result[role] = {'Id': 'sha256:' + str(index + 1) * 64, 'Os': 'linux', 'Architecture': 'amd64',
                        'RootFS': {'Layers': ['sha256:' + 'a' * 64] + (['sha256:' + 'b' * 64] if role in ('operations', 'railway') else [])},
                        'Config': {'User': '0:0' if role == 'railway' else '1654:1654',
                                   'Entrypoint': ['dotnet', 'AeroScience.Organization.dll', '--railway-start'] if role == 'railway' else ['dotnet', 'AeroScience.Organization.dll'],
                                   'Labels': {'org.opencontainers.image.revision': 'c' * 40,
                                              'org.opencontainers.image.source': REVIEW.SOURCE,
                                              'org.opencontainers.image.version': '0.7.0-development',
                                              'com.aeroscience.organization.role': role}, 'Env': ['ASPNETCORE_ENVIRONMENT=Production']}}
    return result


class OrganizationImageReviewTests(unittest.TestCase):
    def test_actual_config_id_is_never_promoted_to_a_registry_digest_or_publication(self):
        report = REVIEW.verify_images(fixture(), 'c' * 40)
        self.assertFalse(report['published'])
        self.assertFalse(report['publicationAuthorized'])
        self.assertFalse(report['productionReady'])
        self.assertIsNone(report['imageSet'])
        self.assertEqual(4, len(report['images']))
        self.assertTrue(all(v['registryManifestDigest'] is None for v in report['images'].values()))

    def test_cross_source_role_platform_and_unsafe_application_images_are_rejected(self):
        for role, section, key, value in [
            ('database', 'Labels', 'org.opencontainers.image.revision', 'd' * 40),
            ('operations', 'Labels', 'com.aeroscience.organization.role', 'base'),
            ('base', 'Labels', 'org.opencontainers.image.source', 'https://example.invalid'),
            ('railway', 'Labels', 'org.opencontainers.image.version', 'unknown'),
            ('operations', None, 'Architecture', 'arm64'),
            ('database', None, 'Os', 'windows'),
            ('base', 'Config', 'User', '0'),
            ('operations', 'Config', 'User', ''),
            ('railway', 'Config', 'Entrypoint', ['/bin/sh']),
            ('railway', 'Config', 'User', 'root'),
            ('database', None, 'Id', 'latest'),
            ('base', 'Config', 'Env', ['Organization__BootstrapSecret=private-value']),
        ]:
            with self.subTest(role=role, key=key):
                data = fixture()
                target = data[role]['Config']['Labels'] if section == 'Labels' else data[role]['Config'] if section == 'Config' else data[role]
                target[key] = value
                with self.assertRaises(ValueError):
                    REVIEW.verify_images(data, 'c' * 40)

    def test_parent_substitution_and_duplicate_identities_are_rejected(self):
        for role in ('operations', 'railway'):
            data = fixture()
            data[role]['RootFS']['Layers'] = ['sha256:' + 'e' * 64]
            with self.assertRaises(ValueError):
                REVIEW.verify_images(data, 'c' * 40)
        data = fixture()
        data['database']['Id'] = data['base']['Id']
        with self.assertRaises(ValueError):
            REVIEW.verify_images(data, 'c' * 40)

    def test_missing_roles_unknown_sources_and_empty_layers_fail_closed(self):
        for revision in ('main', '', 'x' * 40):
            with self.assertRaises(ValueError):
                REVIEW.verify_images(fixture(), revision)
        data = fixture()
        del data['database']
        with self.assertRaises(ValueError):
            REVIEW.verify_images(data, 'c' * 40)
        data = copy.deepcopy(fixture())
        data['base']['RootFS']['Layers'] = []
        with self.assertRaises(ValueError):
            REVIEW.verify_images(data, 'c' * 40)
