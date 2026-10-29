"""Untrusted challenge and native-template candidate invariants; actual runtime tests use Docker/PostgreSQL."""
import base64
import hashlib
import hmac
import importlib.util
import json
from pathlib import Path
import unittest
ROOT=Path(__file__).resolve().parents[1]
SPEC=importlib.util.spec_from_file_location('database_trust',ROOT/'deploy/organization/railway/database-trust.py')
TRUST=importlib.util.module_from_spec(SPEC);SPEC.loader.exec_module(TRUST)
class RailwayDeploymentTests(unittest.TestCase):
    def test_attestation_binds_exact_host_fresh_nonce_and_only_public_certificate(self):
        host='postgres.railway.internal';nonce='b'*64;secret='a'*64;certificate=b'public certificate fixture only'
        response=TRUST.attest(host,nonce,secret,certificate);encoded=base64.b64encode(certificate).decode();message=('aeroscience-railway-database-trust-v1\n'+host+'\n'+nonce+'\n'+encoded).encode()
        self.assertEqual(hmac.new(secret.encode(),message,hashlib.sha256).hexdigest(),response['authentication']);self.assertEqual(encoded,response['certificate']);self.assertNotIn(secret,json.dumps(response))
        for bad in ('127.0.0.1','evil.railway.internal/secret','postgres.railway.internal\nX:bad'):
            with self.assertRaises(ValueError):TRUST.attest(bad,nonce,secret,certificate)
        with self.assertRaises(ValueError):TRUST.attest(host,'repeat',secret,certificate)
    def test_candidate_uses_native_secret_references_without_fake_launch_or_public_database(self):
        value=json.loads((ROOT/'deploy/organization/railway/template.candidate.json').read_text());self.assertFalse(value['launchEnabled']);self.assertIsNone(value['nativeTemplateId']);self.assertLessEqual(len(value['stages']),5);self.assertFalse(value['services']['PostgreSQL']['publicNetworking']);self.assertTrue(value['recovery']['independentDestinationRequired']);self.assertGreater(len(value['acceptanceBlockers']),0)
        for name in ('Organization__BootstrapSecret','Organization__KeyCertificatePassword'):self.assertTrue(value['services']['Organization']['variables'][name].startswith('${{secret('))
        self.assertEqual('${{PostgreSQL.AEROSCIENCE_DATABASE_TRUST_SECRET}}',value['services']['Organization']['variables']['AEROSCIENCE_DATABASE_TRUST_SECRET'])
    def test_recovery_and_application_profiles_keep_pins_and_privilege_boundary(self):
        operations=(ROOT/'deploy/organization/Dockerfile.operations').read_text();self.assertEqual(2,operations.count('@sha256:'));self.assertIn('USER 1654:1654',operations);self.assertIn('restic/restic:0.19.1@sha256:136600b6ff6843d61d355f7f71f460a166429f35de6fd11b568fece3c9a4d510',operations)
        runtime=(ROOT/'src/AeroScience.Organization/RailwayRuntime.cs').read_text();self.assertIn('setresuid(1654,1654,1654)',runtime);self.assertIn('SslMode.VerifyFull',runtime);self.assertNotIn('TrustServerCertificate',runtime)
