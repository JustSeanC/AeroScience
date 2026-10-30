"""P25 configuration integrity; container/real-PostgreSQL acceptance is a separate real smoke test."""
import contextlib
import http.server
import importlib.util
import io
import os
from pathlib import Path
import shutil
import socket
import ssl
import subprocess
import tempfile
import threading
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
            chain=['openssl','verify','-x509_strict','-purpose','sslserver','-CAfile',str(target/'database-ca.crt'),'-verify_hostname']
            verified=subprocess.run(chain+['database',str(target/'database.crt')],capture_output=True,text=True,timeout=10)
            self.assertEqual(0,verified.returncode,verified.stderr)
            wrong_host=subprocess.run(chain+['wrong.database.example',str(target/'database.crt')],capture_output=True,text=True,timeout=10)
            self.assertNotEqual(0,wrong_host.returncode)
            class Receiver(http.server.BaseHTTPRequestHandler):
                def do_GET(self):
                    self.send_response(200);self.end_headers();self.wfile.write(b'Local strict TLS fixture only')
                def log_message(self,*args):pass
            server_context=ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
            server_context.load_cert_chain(target/'database.crt',target/'database.key')
            receiver=http.server.ThreadingHTTPServer(('127.0.0.1',0),Receiver)
            receiver.socket=server_context.wrap_socket(receiver.socket,server_side=True)
            worker=threading.Thread(target=receiver.serve_forever,kwargs={'poll_interval':0.05},daemon=True);worker.start()
            try:
                client=ssl.create_default_context(cafile=str(target/'database-ca.crt'));client.verify_flags|=ssl.VERIFY_X509_STRICT
                self.assertTrue(client.check_hostname);self.assertEqual(ssl.CERT_REQUIRED,client.verify_mode)
                with socket.create_connection(receiver.server_address,timeout=3) as raw:
                    with client.wrap_socket(raw,server_hostname='database') as secure:
                        secure.sendall(b'GET /health HTTP/1.1\r\nHost: database\r\nConnection: close\r\n\r\n')
                        self.assertIn(b'200 OK',secure.recv(1024))
                for context,hostname in [(client,'wrong.database.example'),(ssl.create_default_context(),'database')]:
                    with socket.create_connection(receiver.server_address,timeout=3) as raw:
                        with self.assertRaises(ssl.SSLCertVerificationError):context.wrap_socket(raw,server_hostname=hostname)
            finally:
                receiver.shutdown();receiver.server_close();worker.join(timeout=3);self.assertFalse(worker.is_alive())
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
