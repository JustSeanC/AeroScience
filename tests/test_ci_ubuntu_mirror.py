import os
import importlib.util
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('prepare_ci_ubuntu_mirror',
    Path(__file__).resolve().parents[1] / 'scripts/build/prepare_ci_ubuntu_mirror.py')
mirror = importlib.util.module_from_spec(spec)
spec.loader.exec_module(mirror)


class CiUbuntuMirrorTests(unittest.TestCase):
    original = b'http://azure.archive.ubuntu.com/ubuntu priority:1\nhttps://archive.ubuntu.com/ubuntu priority:2\nhttps://security.ubuntu.com/ubuntu priority:3\n'

    def metadata(self, suite='noble', origin='Ubuntu', codename='noble'):
        return ('-----BEGIN PGP SIGNED MESSAGE-----\nHash: SHA512\n\nOrigin: '+origin+'\nSuite: '+suite+'\nCodename: '+codename+'\n').encode()

    def test_all_suite_signatures_are_required_before_preserving_and_extending_original_list(self):
        with tempfile.TemporaryDirectory() as folder:
            path=Path(folder)/'mirrors';path.write_bytes(self.original);seen=[]
            def verify(suite):
                self.assertEqual(path.read_bytes(),self.original);seen.append(suite);return 'verified-test-fixture'
            self.assertTrue(mirror.prepare(path,verify=verify,log=lambda message:None))
            self.assertEqual(seen,list(mirror.SUITES));self.assertTrue(path.read_bytes().startswith(self.original))
            self.assertEqual(path.read_text().count(mirror.ENTRY),1)
            self.assertFalse(mirror.prepare(path,verify=lambda suite:self.fail('Repeated verification'),log=lambda message:None))

    def test_invalid_signature_or_suite_failure_never_changes_original_list(self):
        with tempfile.TemporaryDirectory() as folder:
            path=Path(folder)/'mirrors';path.write_bytes(self.original)
            def refuse(suite):
                if suite=='noble-security':raise subprocess.CalledProcessError(1,'gpgv')
                return 'earlier-suite-only'
            with self.assertRaises(subprocess.CalledProcessError):mirror.prepare(path,verify=refuse,log=lambda message:None)
            self.assertEqual(path.read_bytes(),self.original)

    def test_custom_sources_are_refused_without_network_or_mutation(self):
        with tempfile.TemporaryDirectory() as folder:
            path=Path(folder)/'mirrors';data=b'https://custom.example/ubuntu priority:1\n';path.write_bytes(data)
            with self.assertRaises(ValueError):mirror.prepare(path,verify=lambda suite:self.fail('Unexpected network'))
            self.assertEqual(path.read_bytes(),data)

    def test_signed_identity_wrong_origin_suite_codename_and_oversize_refused(self):
        for body in [self.metadata(origin='Other'),self.metadata(suite='other'),self.metadata(codename='other'),b'<html>error</html>',b'x'*1048577]:
            with self.subTest(body=body[:100]),self.assertRaises(ValueError):
                mirror.verify_release('noble',download=lambda url:body,verify=lambda data:None)
        with self.assertRaises(ValueError):mirror.verify_release('arbitrary',download=lambda url:self.fail('Unexpected network'))

    def test_actual_signature_command_uses_existing_ubuntu_keyring_and_checks_failure(self):
        with patch.object(mirror.subprocess,'run') as run:
            mirror.verify_release('noble',download=lambda url:self.metadata())
            args=run.call_args
            self.assertEqual(args.args[0][:3],['gpgv','--keyring',mirror.KEYRING]);self.assertTrue(args.kwargs['check']);self.assertEqual(args.kwargs['timeout'],15)
        with patch.object(mirror.subprocess,'run',side_effect=subprocess.CalledProcessError(1,'gpgv')):
            with self.assertRaises(subprocess.CalledProcessError):mirror.verify_release('noble',download=lambda url:self.metadata())

    def test_https_download_is_fixed_bounded_verified_by_default_and_redirects_refused(self):
        class Response:
            def __enter__(self):return self
            def __exit__(self,*args):pass
            def read(self,bound):self.bound=bound;return b'-----BEGIN PGP SIGNED MESSAGE-----\nOrigin: Ubuntu\nSuite: noble\nCodename: noble\n'
        response=Response()
        with patch.object(mirror.urllib.request,'build_opener') as build:
            build.return_value.open.return_value=response;mirror.verify_release('noble',verify=lambda body:None)
            self.assertEqual(build.return_value.open.call_args.args[0],mirror.MIRROR+'dists/noble/InRelease');self.assertEqual(response.bound,1048577)
            self.assertIsInstance(build.call_args.args[0],mirror.NoRedirect)
        self.assertIsNone(mirror.NoRedirect().redirect_request(None,None,302,'',{},'http://untrusted.example'))

    def test_non_ci_and_non_linux_refused_before_accessing_any_mirror_file(self):
        for env in [{'GITHUB_ACTIONS':'false','RUNNER_OS':'Linux'},{'GITHUB_ACTIONS':'true','RUNNER_OS':'Windows'}]:
            with patch.dict(os.environ,env),patch.object(mirror,'prepare') as prepare,self.assertRaises(SystemExit):mirror.main()
            prepare.assert_not_called()
