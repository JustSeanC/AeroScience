"""P25 configuration integrity; container/real-PostgreSQL acceptance is a separate real smoke test."""
import contextlib
import importlib.util
import io
import os
from pathlib import Path
import shutil
import tempfile
import unittest

ROOT=Path(__file__).resolve().parents[1]
SPEC=importlib.util.spec_from_file_location('organization_prepare',ROOT/'deploy/organization/self-hosted/prepare.py')
PREPARE=importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(PREPARE)

class OrganizationDeploymentTests(unittest.TestCase):
    def test_advanced_host_validation_cannot_become_shell_or_config_input(self):
        with tempfile.TemporaryDirectory() as temporary:
            for host in ('http://example.org','example.org/path','example.org\nSECRET=bad','$(whoami).org','localhost','UPPER.example.org'):
                with self.subTest(host=host):
                    with self.assertRaises(ValueError):PREPARE.prepare(Path(temporary)/'private',host)
                    self.assertFalse((Path(temporary)/'private').exists())

    @unittest.skipUnless(shutil.which('openssl') and os.name!='nt','POSIX OpenSSL/mode fixture; real Linux container acceptance verifies certificates.')
    def test_generated_private_configuration_is_unique_bounded_and_never_prints_credentials(self):
        with tempfile.TemporaryDirectory() as temporary:
            target=Path(temporary)/'protected'
            captured=io.StringIO()
            with contextlib.redirect_stdout(captured):result=PREPARE.prepare(target,'organization.example.org')
            self.assertEqual('',captured.getvalue());self.assertFalse(result['productionReady'])
            values=dict(line.split('=',1) for line in (target/'application.env').read_text().splitlines())
            self.assertNotEqual(values['Organization__BootstrapSecret'],values['Organization__KeyCertificatePassword'])
            self.assertEqual(64,len(values['Organization__BootstrapSecret']))
            self.assertIn('SSL Mode=VerifyFull',values['Organization__ConnectionString'])
            self.assertIn('Root Certificate=',values['Organization__ConnectionString'])
            self.assertEqual(0o700,target.stat().st_mode&0o777)
            for name in ('application.env','database-owner-password','database.key','database-ca.key','initialize.sql'):
                self.assertEqual(0o600,(target/name).stat().st_mode&0o777)
            before=(target/'application.env').read_bytes()
            with self.assertRaises(ValueError):PREPARE.prepare(target,'organization.example.org')
            self.assertEqual(before,(target/'application.env').read_bytes())

    def test_container_is_pinned_non_root_and_never_copies_local_archives(self):
        docker=(ROOT/'deploy/organization/Dockerfile').read_text()
        self.assertEqual(2,docker.count('@sha256:'))
        self.assertIn('USER 1654:1654',docker);self.assertIn('--locked-mode',docker)
        self.assertIn('SOURCE_REVISION',docker);self.assertIn('--health-check',docker)
        self.assertNotIn('COPY . .',docker)
        ignore=(ROOT/'.dockerignore').read_text()
        for forbidden in ('**/*.pfx','**/*.key','**/.env*','**/bin/**','**/obj/**'):self.assertIn(forbidden,ignore)

    def test_provider_candidate_does_not_declare_free_database_or_unrestricted_trust(self):
        candidate=(ROOT/'deploy/organization/render/render.candidate.yaml').read_text()
        self.assertIn('deployment candidate', (ROOT/'deploy/organization/render/README.md').read_text())
        self.assertIn('plan: 0.1c-256mb',candidate)
        self.assertNotIn('plan: free',candidate)
        self.assertIn('ipAllowList: []',candidate)
        self.assertNotIn('0.0.0.0/0',candidate.split('ipAllowList:')[1].split('\n')[0])
        self.assertIn('generateValue: true',candidate)

if __name__=='__main__':unittest.main()
